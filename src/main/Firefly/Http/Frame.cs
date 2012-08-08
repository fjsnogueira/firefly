﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Firefly.Streams;
using Firefly.Utils;
using Owin;

// ReSharper disable AccessToModifiedClosure

namespace Firefly.Http
{
    public enum ProduceEndType
    {
        SocketShutdownSend,
        SocketDisconnect,
        ConnectionKeepAlive,
    }

    public struct FrameContext
    {
        public IFireflyService Services;
        public AppDelegate App;
        public Func<ArraySegment<byte>, bool> Write;
        public Func<Action, bool> Flush;
        public Action<ProduceEndType> End;
    }

    public class Frame
    {
        private FrameContext _context;

        Mode _mode;

        enum Mode
        {
            StartLine,
            MessageHeader,
            MessageBody,
            Terminated,
        }

        private string _method;
        private string _requestUri;
        private string _path;
        private string _queryString;
        private string _httpVersion;

        private readonly IDictionary<string, string[]> _headers =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        private MessageBody _messageBody;
        private bool _resultStarted;
        private bool _keepAlive;

        public Frame(FrameContext context)
        {
            _context = context;
        }

        public bool LocalIntakeFin
        {
            get
            {
                return _mode == Mode.MessageBody
                    ? _messageBody.LocalIntakeFin
                    : _mode == Mode.Terminated;
            }
        }

        public bool Consume(Baton baton, Action<Frame> callback, Action<Exception> fault)
        {
            for (; ; )
            {
                switch (_mode)
                {
                    case Mode.StartLine:
                        if (baton.RemoteIntakeFin)
                        {
                            _mode = Mode.Terminated;
                            return false;
                        }

                        if (!TakeStartLine(baton))
                        {
                            return false;
                        }

                        _mode = Mode.MessageHeader;
                        break;

                    case Mode.MessageHeader:
                        if (baton.RemoteIntakeFin)
                        {
                            _mode = Mode.Terminated;
                            return false;
                        }

                        var endOfHeaders = false;
                        while (!endOfHeaders)
                        {
                            if (!TakeMessageHeader(baton, out endOfHeaders))
                            {
                                return false;
                            }
                        }

                        var resumeBody = HandleExpectContinue(callback);
                        _messageBody = MessageBody.For(
                            _httpVersion,
                            _headers,
                            () =>
                            {
                                if (!Consume(baton, resumeBody, fault))
                                {
                                    resumeBody.Invoke(this);
                                }
                            });
                        _keepAlive = _messageBody.RequestKeepAlive;
                        _mode = Mode.MessageBody;
                        baton.Free();
                        Execute();
                        return true;

                    case Mode.MessageBody:
                        return _messageBody.Consume(baton, () => callback(this), fault);

                    case Mode.Terminated:
                        return false;
                }
            }
        }

        Action<Frame> HandleExpectContinue(Action<Frame> continuation)
        {
            string[] expect;
            if (_httpVersion.Equals("HTTP/1.1") &&
                _headers.TryGetValue("Expect", out expect) &&
                    (expect.FirstOrDefault() ?? "").Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                return frame =>
                {
                    if (_resultStarted)
                    {
                        continuation.Invoke(frame);
                    }
                    else
                    {
                        var bytes = Encoding.Default.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        var isasync =
                            _context.Write(new ArraySegment<byte>(bytes)) &&
                                _context.Flush(() => continuation(frame));
                        if (!isasync)
                        {
                            continuation.Invoke(frame);
                        }
                    }
                };
            }
            return continuation;
        }

        private void Execute()
        {
            _context.App(CreateCallParameters())
                .Then(result =>
                {
                    _resultStarted = true;

                    var status = ReasonPhrases.ToStatus(result.Status, GetReasonPhrase(result.Properties));
                    var responseHeader = CreateResponseHeader(status, result.Headers);
                    var buffering = _context.Write(responseHeader.Item1);
                    responseHeader.Item2.Dispose();

                    var tcs = new TaskCompletionSource<Func<Stream, Task>>();
                    if (!buffering || !_context.Flush(() => tcs.SetResult(result.Body)))
                    {
                        tcs.SetResult(result.Body);
                    }
                    return tcs.Task;
                })
                .Then(body => body(new OutputStream(_context.Write, _context.Flush)))
                .Then(() => ProduceEnd(null))
                .Catch(info =>
                    {
                        ProduceEnd(info.Exception);
                        return info.Handled();
                    });
        }

        static string GetReasonPhrase(IDictionary<string, object> properties)
        {
            string reasonPhrase = null;
            object reasonPhraseValue;
            if (properties != null &&
                properties.TryGetValue("owin.ReasonPhrase", out reasonPhraseValue) &&
                reasonPhraseValue != null)
            {
                reasonPhrase = Convert.ToString(reasonPhraseValue);
            }
            return reasonPhrase;
        }

        private void ProduceEnd(Exception ex)
        {
            if (!_keepAlive)
            {
                _context.End(ProduceEndType.SocketShutdownSend);
            }

            if (!_messageBody.Drain(
                () => _context.End(_keepAlive ? ProduceEndType.ConnectionKeepAlive : ProduceEndType.SocketDisconnect)))
            {
                _context.End(_keepAlive ? ProduceEndType.ConnectionKeepAlive : ProduceEndType.SocketDisconnect);
            }
        }


        private Tuple<ArraySegment<byte>, IDisposable> CreateResponseHeader(
            string status, IEnumerable<KeyValuePair<string, string[]>> headers)
        {
            var writer = new MemoryPoolTextWriter(_context.Services.Memory);
            writer.Write(_httpVersion);
            writer.Write(' ');
            writer.Write(status);
            writer.Write('\r');
            writer.Write('\n');

            var hasConnection = false;
            var hasTransferEncoding = false;
            var hasContentLength = false;
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    var isConnection = false;
                    if (!hasConnection &&
                        string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConnection = isConnection = true;
                    }
                    else if (!hasTransferEncoding &&
                        string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTransferEncoding = true;
                    }
                    else if (!hasContentLength &&
                        string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        hasContentLength = true;
                    }

                    foreach (var value in header.Value)
                    {
                        writer.Write(header.Key);
                        writer.Write(':');
                        writer.Write(' ');
                        writer.Write(value);
                        writer.Write('\r');
                        writer.Write('\n');

                        if (isConnection && value.IndexOf("close", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            _keepAlive = false;
                        }
                    }
                }
            }

            if (hasTransferEncoding == false && hasContentLength == false)
            {
                _keepAlive = false;
            }
            if (_keepAlive == false && hasConnection == false && _httpVersion == "HTTP/1.1")
            {
                writer.Write("Connection: close\r\n\r\n");
            }
            else if (_keepAlive && hasConnection == false && _httpVersion == "HTTP/1.0")
            {
                writer.Write("Connection: keep-alive\r\n\r\n");
            }
            else
            {
                writer.Write('\r');
                writer.Write('\n');
            }
            writer.Flush();
            return new Tuple<ArraySegment<byte>, IDisposable>(writer.Buffer, writer);
        }

        private bool TakeStartLine(Baton baton)
        {
            var remaining = baton.Buffer;
            if (remaining.Count < 2)
            {
                return false;
            }
            var firstSpace = -1;
            var secondSpace = -1;
            var questionMark = -1;
            var ch0 = remaining.Array[remaining.Offset];
            for (var index = 0; index != remaining.Count - 1; ++index)
            {
                var ch1 = remaining.Array[remaining.Offset + index + 1];
                if (ch0 == '\r' && ch1 == '\n')
                {
                    if (secondSpace == -1)
                    {
                        throw new InvalidOperationException("INVALID REQUEST FORMAT");
                    }
                    _method = GetString(remaining, 0, firstSpace);
                    _requestUri = GetString(remaining, firstSpace + 1, secondSpace);
                    if (questionMark == -1)
                    {
                        _path = _requestUri;
                        _queryString = string.Empty;
                    }
                    else
                    {
                        _path = GetString(remaining, firstSpace + 1, questionMark);
                        _queryString = GetString(remaining, questionMark + 1, secondSpace);
                    }
                    _httpVersion = GetString(remaining, secondSpace + 1, index);
                    baton.Skip(index + 2);
                    return true;
                }

                if (ch0 == ' ' && firstSpace == -1)
                {
                    firstSpace = index;
                }
                else if (ch0 == ' ' && firstSpace != -1 && secondSpace == -1)
                {
                    secondSpace = index;
                }
                else if (ch0 == '?' && firstSpace != -1 && questionMark == -1 && secondSpace == -1)
                {
                    questionMark = index;
                }
                ch0 = ch1;
            }
            return false;
        }

        static string GetString(ArraySegment<byte> range, int startIndex, int endIndex)
        {
            return Encoding.Default.GetString(range.Array, range.Offset + startIndex, endIndex - startIndex);
        }


        private bool TakeMessageHeader(Baton baton, out bool endOfHeaders)
        {
            var remaining = baton.Buffer;
            endOfHeaders = false;
            if (remaining.Count < 2)
            {
                return false;
            }
            var ch0 = remaining.Array[remaining.Offset];
            var ch1 = remaining.Array[remaining.Offset + 1];
            if (ch0 == '\r' && ch1 == '\n')
            {
                endOfHeaders = true;
                baton.Skip(2);
                return true;
            }

            if (remaining.Count < 3)
            {
                return false;
            }
            var wrappedHeaders = false;
            var colonIndex = -1;
            var valueStartIndex = -1;
            var valueEndIndex = -1;
            for (var index = 0; index != remaining.Count - 2; ++index)
            {
                var ch2 = remaining.Array[remaining.Offset + index + 2];
                if (ch0 == '\r' &&
                    ch1 == '\n' &&
                        ch2 != ' ' &&
                            ch2 != '\t')
                {
                    var name = Encoding.ASCII.GetString(remaining.Array, remaining.Offset, colonIndex);
                    var value = "";
                    if (valueEndIndex != -1)
                    {
                        value = Encoding.ASCII.GetString(
                            remaining.Array, remaining.Offset + valueStartIndex, valueEndIndex - valueStartIndex);
                    }
                    if (wrappedHeaders)
                    {
                        value = value.Replace("\r\n", " ");
                    }
                    AddRequestHeader(name, value);
                    baton.Skip(index + 2);
                    return true;
                }
                if (colonIndex == -1 && ch0 == ':')
                {
                    colonIndex = index;
                }
                else if (colonIndex != -1 &&
                    ch0 != ' ' &&
                        ch0 != '\t' &&
                            ch0 != '\r' &&
                                ch0 != '\n')
                {
                    if (valueStartIndex == -1)
                    {
                        valueStartIndex = index;
                    }
                    valueEndIndex = index + 1;
                }
                else if (!wrappedHeaders &&
                    ch0 == '\r' &&
                        ch1 == '\n' &&
                            (ch2 == ' ' ||
                                ch2 == '\t'))
                {
                    wrappedHeaders = true;
                }

                ch0 = ch1;
                ch1 = ch2;
            }
            return false;
        }


        private void AddRequestHeader(string name, string value)
        {
            string[] existing;
            if (!_headers.TryGetValue(name, out existing) ||
                existing == null ||
                existing.Length == 0)
            {
                _headers[name] = new[] { value };
            }
            else
            {
                _headers[name] = existing.Concat(new[] { value }).ToArray();
            }
        }

        private CallParameters CreateCallParameters()
        {
            IDictionary<string, object> env = new Dictionary<string, object>();
            env["owin.RequestMethod"] = _method;
            env["owin.RequestPath"] = _path;
            env["owin.RequestPathBase"] = "";
            env["owin.RequestQueryString"] = _queryString;
            //env["owin.RequestHeaders"] = _headers;
            //env["owin.RequestBody"] = (BodyDelegate)_messageBody.Subscribe;
            env["owin.RequestScheme"] = "http"; // TODO: pass along information about scheme, cgi headers, etc
            env["owin.Version"] = "1.0";
            return new CallParameters
            {
                Environment = env,
                Headers = _headers,
                Body = new InputStream(_messageBody.Subscribe),
            };
        }
    }
}
