// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http
{
    /// <summary>
    ///   http://tools.ietf.org/html/rfc2616#section-3.6.1
    /// </summary>
    public class ForChunkedEncoding : Http1MessageBody
    {
        // byte consts don't have a data type annotation so we pre-cast it
        private const byte ByteCR = (byte)'\r';
        // "7FFFFFFF\r\n" is the largest chunk size that could be returned as an int.
        private const int MaxChunkPrefixBytes = 10;

        private long _inputLength;

        private Mode _mode = Mode.Prefix;
        private volatile bool _canceled;
        private Task _pumpTask;
        private Pipe _requestBodyPipe;

        public ForChunkedEncoding(bool keepAlive, Http1Connection context)
            : base(context)
        {
            RequestKeepAlive = keepAlive;

            // For now, chunking will use the request body pipe
            _requestBodyPipe = CreateRequestBodyPipe(context);
            //context.InternalRequestBodyPipeReader = _requestBodyPipe.Reader;
        }

        private Pipe CreateRequestBodyPipe(Http1Connection context)
            => new Pipe(new PipeOptions
            (
                pool: context.MemoryPool,
                readerScheduler: context.ServiceContext.Scheduler,
                writerScheduler: PipeScheduler.Inline,
                pauseWriterThreshold: 1,
                resumeWriterThreshold: 1,
                useSynchronizationContext: false,
                minimumSegmentSize: KestrelMemoryPool.MinimumSegmentSize
            ));

        private async Task PumpAsync()
        {
            Debug.Assert(!RequestUpgrade, "Upgraded connections should never use this code path!");

            Exception error = null;

            try
            {
                var awaitable = _context.Input.ReadAsync();

                if (!awaitable.IsCompleted)
                {
                    TryProduceContinue();
                }

                while (true)
                {
                    var result = await awaitable;

                    if (_context.RequestTimedOut)
                    {
                        BadHttpRequestException.Throw(RequestRejectionReason.RequestBodyTimeout);
                    }

                    var readableBuffer = result.Buffer;
                    var consumed = readableBuffer.Start;
                    var examined = readableBuffer.Start;

                    try
                    {
                        if (_canceled)
                        {
                            break;
                        }

                        if (!readableBuffer.IsEmpty)
                        {
                            bool done;
                            done = Read(readableBuffer, _requestBodyPipe.Writer, out consumed, out examined);

                            await _requestBodyPipe.Writer.FlushAsync();

                            if (done)
                            {
                                break;
                            }
                        }

                        // Read() will have already have greedily consumed the entire request body if able.
                        if (result.IsCompleted)
                        {
                            // OnInputOrOutputCompleted() is an idempotent method that closes the connection. Sometimes
                            // input completion is observed here before the Input.OnWriterCompleted() callback is fired,
                            // so we call OnInputOrOutputCompleted() now to prevent a race in our tests where a 400
                            // response is written after observing the unexpected end of request content instead of just
                            // closing the connection without a response as expected.
                            _context.OnInputOrOutputCompleted();

                            BadHttpRequestException.Throw(RequestRejectionReason.UnexpectedEndOfRequestContent);
                        }
                    }
                    finally
                    {
                        _context.Input.AdvanceTo(consumed, examined);
                    }

                    awaitable = _context.Input.ReadAsync();
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                _requestBodyPipe.Writer.Complete(error);
            }
        }

        protected override Task OnStopAsync()
        {
            if (!_context.HasStartedConsumingRequestBody)
            {
                return Task.CompletedTask;
            }

            // call complete here on the reader
            _requestBodyPipe.Reader.Complete();

            // PumpTask catches all Exceptions internally.
            if (_pumpTask.IsCompleted)
            {
                // At this point both the request body pipe reader and writer should be completed.
                _requestBodyPipe.Reset();
                return Task.CompletedTask;
            }

            // Should I call complete here?
            return StopAsyncAwaited();
        }

        private async Task StopAsyncAwaited()
        {
            _canceled = true;
            _context.Input.CancelPendingRead();
            await _pumpTask;

            // At this point both the request body pipe reader and writer should be completed.
            _requestBodyPipe.Reset();
        }

        protected override Task OnConsumeAsync()
        {
            try
            {
                if (_requestBodyPipe.Reader.TryRead(out var readResult))
                {
                    _requestBodyPipe.Reader.AdvanceTo(readResult.Buffer.End);

                    if (readResult.IsCompleted)
                    {
                        return Task.CompletedTask;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // TryRead can throw OperationCanceledException https://github.com/dotnet/corefx/issues/32029
                // because of buggy logic, this works around that for now
            }
            catch (BadHttpRequestException ex)
            {
                // At this point, the response has already been written, so this won't result in a 4XX response;
                // however, we still need to stop the request processing loop and log.
                _context.SetBadRequestState(ex);
                return Task.CompletedTask;
            }

            return OnConsumeAsyncAwaited();
        }

        private async Task OnConsumeAsyncAwaited()
        {
            Log.RequestBodyNotEntirelyRead(_context.ConnectionIdFeature, _context.TraceIdentifier);

            _context.TimeoutControl.SetTimeout(Constants.RequestBodyDrainTimeout.Ticks, TimeoutReason.RequestBodyDrain);

            try
            {
                ReadResult result;
                do
                {
                    result = await _requestBodyPipe.Reader.ReadAsync();
                    _requestBodyPipe.Reader.AdvanceTo(result.Buffer.End);
                } while (!result.IsCompleted);
            }
            catch (BadHttpRequestException ex)
            {
                _context.SetBadRequestState(ex);
            }
            catch (ConnectionAbortedException)
            {
                Log.RequestBodyDrainTimedOut(_context.ConnectionIdFeature, _context.TraceIdentifier);
            }
            finally
            {
                _context.TimeoutControl.CancelTimeout();
            }
        }

        protected void Copy(ReadOnlySequence<byte> readableBuffer, PipeWriter writableBuffer)
        {
            if (readableBuffer.IsSingleSegment)
            {
                writableBuffer.Write(readableBuffer.First.Span);
            }
            else
            {
                foreach (var memory in readableBuffer)
                {
                    writableBuffer.Write(memory.Span);
                }
            }
        }

        protected override void OnReadStarted()
        {
            _pumpTask = PumpAsync();
        }

        protected bool Read(ReadOnlySequence<byte> readableBuffer, PipeWriter writableBuffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = default;
            examined = default;

            while (_mode < Mode.Trailer)
            {
                if (_mode == Mode.Prefix)
                {
                    ParseChunkedPrefix(readableBuffer, out consumed, out examined);

                    if (_mode == Mode.Prefix)
                    {
                        return false;
                    }

                    readableBuffer = readableBuffer.Slice(consumed);
                }

                if (_mode == Mode.Extension)
                {
                    ParseExtension(readableBuffer, out consumed, out examined);

                    if (_mode == Mode.Extension)
                    {
                        return false;
                    }

                    readableBuffer = readableBuffer.Slice(consumed);
                }

                if (_mode == Mode.Data)
                {
                    ReadChunkedData(readableBuffer, writableBuffer, out consumed, out examined);

                    if (_mode == Mode.Data)
                    {
                        return false;
                    }

                    readableBuffer = readableBuffer.Slice(consumed);
                }

                if (_mode == Mode.Suffix)
                {
                    ParseChunkedSuffix(readableBuffer, out consumed, out examined);

                    if (_mode == Mode.Suffix)
                    {
                        return false;
                    }

                    readableBuffer = readableBuffer.Slice(consumed);
                }
            }

            // Chunks finished, parse trailers
            if (_mode == Mode.Trailer)
            {
                ParseChunkedTrailer(readableBuffer, out consumed, out examined);

                if (_mode == Mode.Trailer)
                {
                    return false;
                }

                readableBuffer = readableBuffer.Slice(consumed);
            }

            // _consumedBytes aren't tracked for trailer headers, since headers have separate limits.
            if (_mode == Mode.TrailerHeaders)
            {
                if (_context.TakeMessageHeaders(readableBuffer, out consumed, out examined))
                {
                    _mode = Mode.Complete;
                }
            }

            return _mode == Mode.Complete;
        }

        private void ParseChunkedPrefix(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.Start;
            var reader = new BufferReader(buffer);
            var ch1 = reader.Read();
            var ch2 = reader.Read();

            if (ch1 == -1 || ch2 == -1)
            {
                examined = reader.Position;
                return;
            }

            var chunkSize = CalculateChunkSize(ch1, 0);
            ch1 = ch2;

            while (reader.ConsumedBytes < MaxChunkPrefixBytes)
            {
                if (ch1 == ';')
                {
                    consumed = reader.Position;
                    examined = reader.Position;

                    AddAndCheckConsumedBytes(reader.ConsumedBytes);
                    _inputLength = chunkSize;
                    _mode = Mode.Extension;
                    return;
                }

                ch2 = reader.Read();
                if (ch2 == -1)
                {
                    examined = reader.Position;
                    return;
                }

                if (ch1 == '\r' && ch2 == '\n')
                {
                    consumed = reader.Position;
                    examined = reader.Position;

                    AddAndCheckConsumedBytes(reader.ConsumedBytes);
                    _inputLength = chunkSize;
                    _mode = chunkSize > 0 ? Mode.Data : Mode.Trailer;
                    return;
                }

                chunkSize = CalculateChunkSize(ch1, chunkSize);
                ch1 = ch2;
            }

            // At this point, 10 bytes have been consumed which is enough to parse the max value "7FFFFFFF\r\n".
            BadHttpRequestException.Throw(RequestRejectionReason.BadChunkSizeData);
        }

        private void ParseExtension(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            // Chunk-extensions not currently parsed
            // Just drain the data
            consumed = buffer.Start;
            examined = buffer.Start;

            do
            {
                SequencePosition? extensionCursorPosition = buffer.PositionOf(ByteCR);
                if (extensionCursorPosition == null)
                {
                    // End marker not found yet
                    consumed = buffer.End;
                    examined = buffer.End;
                    AddAndCheckConsumedBytes(buffer.Length);
                    return;
                };

                var extensionCursor = extensionCursorPosition.Value;
                var charsToByteCRExclusive = buffer.Slice(0, extensionCursor).Length;

                var suffixBuffer = buffer.Slice(extensionCursor);
                if (suffixBuffer.Length < 2)
                {
                    consumed = extensionCursor;
                    examined = buffer.End;
                    AddAndCheckConsumedBytes(charsToByteCRExclusive);
                    return;
                }

                suffixBuffer = suffixBuffer.Slice(0, 2);
                var suffixSpan = suffixBuffer.ToSpan();

                if (suffixSpan[1] == '\n')
                {
                    // We consumed the \r\n at the end of the extension, so switch modes.
                    _mode = _inputLength > 0 ? Mode.Data : Mode.Trailer;

                    consumed = suffixBuffer.End;
                    examined = suffixBuffer.End;
                    AddAndCheckConsumedBytes(charsToByteCRExclusive + 2);
                }
                else
                {
                    // Don't consume suffixSpan[1] in case it is also a \r.
                    buffer = buffer.Slice(charsToByteCRExclusive + 1);
                    consumed = extensionCursor;
                    AddAndCheckConsumedBytes(charsToByteCRExclusive + 1);
                }
            } while (_mode == Mode.Extension);
        }

        private void ReadChunkedData(ReadOnlySequence<byte> buffer, PipeWriter writableBuffer, out SequencePosition consumed, out SequencePosition examined)
        {
            var actual = Math.Min(buffer.Length, _inputLength);
            consumed = buffer.GetPosition(actual);
            examined = consumed;

            Copy(buffer.Slice(0, actual), writableBuffer);

            _inputLength -= actual;
            AddAndCheckConsumedBytes(actual);

            if (_inputLength == 0)
            {
                _mode = Mode.Suffix;
            }
        }

        private void ParseChunkedSuffix(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.Start;

            if (buffer.Length < 2)
            {
                examined = buffer.End;
                return;
            }

            var suffixBuffer = buffer.Slice(0, 2);
            var suffixSpan = suffixBuffer.ToSpan();
            if (suffixSpan[0] == '\r' && suffixSpan[1] == '\n')
            {
                consumed = suffixBuffer.End;
                examined = suffixBuffer.End;
                AddAndCheckConsumedBytes(2);
                _mode = Mode.Prefix;
            }
            else
            {
                BadHttpRequestException.Throw(RequestRejectionReason.BadChunkSuffix);
            }
        }

        private void ParseChunkedTrailer(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.Start;

            if (buffer.Length < 2)
            {
                examined = buffer.End;
                return;
            }

            var trailerBuffer = buffer.Slice(0, 2);
            var trailerSpan = trailerBuffer.ToSpan();

            if (trailerSpan[0] == '\r' && trailerSpan[1] == '\n')
            {
                consumed = trailerBuffer.End;
                examined = trailerBuffer.End;
                AddAndCheckConsumedBytes(2);
                _mode = Mode.Complete;
            }
            else
            {
                _mode = Mode.TrailerHeaders;
            }
        }

        private int CalculateChunkSize(int extraHexDigit, int currentParsedSize)
        {
            try
            {
                checked
                {
                    if (extraHexDigit >= '0' && extraHexDigit <= '9')
                    {
                        return currentParsedSize * 0x10 + (extraHexDigit - '0');
                    }
                    else if (extraHexDigit >= 'A' && extraHexDigit <= 'F')
                    {
                        return currentParsedSize * 0x10 + (extraHexDigit - ('A' - 10));
                    }
                    else if (extraHexDigit >= 'a' && extraHexDigit <= 'f')
                    {
                        return currentParsedSize * 0x10 + (extraHexDigit - ('a' - 10));
                    }
                }
            }
            catch (OverflowException ex)
            {
                throw new IOException(CoreStrings.BadRequest_BadChunkSizeData, ex);
            }

            BadHttpRequestException.Throw(RequestRejectionReason.BadChunkSizeData);
            return -1; // can't happen, but compiler complains
        }
        private ReadResult _previousReadResult;

        public override void AdvanceTo(SequencePosition consumed)
        {
            AdvanceTo(consumed, consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var dataLength = _previousReadResult.Buffer.Slice(_previousReadResult.Buffer.Start, consumed).Length;
            _requestBodyPipe.Reader.AdvanceTo(consumed, examined);
            OnDataRead(dataLength);
        }

        public override bool TryRead(out ReadResult readResult)
        {
            TryStart();

            var res =_requestBodyPipe.Reader.TryRead(out _previousReadResult);
            readResult = _previousReadResult;

            if (_previousReadResult.IsCompleted)
            {
                TryStop();
            }
            return res;
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            TryStart();

            while (true)
            {
                _previousReadResult = await StartTimingReadAsync(cancellationToken);
                var readableBuffer = _previousReadResult.Buffer;
                var readableBufferLength = readableBuffer.Length;
                StopTimingRead(readableBufferLength);

                if (readableBufferLength != 0)
                {
                    break;
                }

                if (_previousReadResult.IsCompleted)
                {
                    TryStop();
                    break;
                }
            }

            return _previousReadResult;
        }

        private ValueTask<ReadResult> StartTimingReadAsync(CancellationToken cancellationToken)
        {
            // The only difference is which reader to use. Let's do the following.
            // Make an internal reader that will always be used for whatever operation is needed here
            // Keep external one the same always.
            var readAwaitable = _requestBodyPipe.Reader.ReadAsync(cancellationToken);

            if (!readAwaitable.IsCompleted && _timingEnabled)
            {
                _backpressure = true;
                _context.TimeoutControl.StartTimingRead();
            }

            return readAwaitable;
        }

        private void StopTimingRead(long bytesRead)
        {
            _context.TimeoutControl.BytesRead(bytesRead - _alreadyTimedBytes);
            _alreadyTimedBytes = 0;

            if (_backpressure)
            {
                _backpressure = false;
                _context.TimeoutControl.StopTimingRead();
            }
        }

        public override void Complete(Exception exception)
        {
            _requestBodyPipe.Reader.Complete(exception);
        }

        public override void OnWriterCompleted(Action<Exception, object> callback, object state)
        {
            throw new NotImplementedException();
        }

        public override void CancelPendingRead()
        {
            throw new NotImplementedException();
        }

        private enum Mode
        {
            Prefix,
            Extension,
            Data,
            Suffix,
            Trailer,
            TrailerHeaders,
            Complete
        };
    }
}
