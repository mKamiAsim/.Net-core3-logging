using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetCore3Log
{
    public class LoggingMiddleware : IMiddleware
    {
        ILogger<LoggingMiddleware> _logger;
        private const int MaxStackLength = 128;
        private const byte Comma = (byte)',';
        public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
        {
            _logger = logger;
        }
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            await LogRequest(context).ConfigureAwait(false);
            await next(context);
        }

        private async Task LogRequest(HttpContext context)
        {

            var request = context.Request;

            var reader = context.Request.BodyReader;
            var cancellationToken = context.RequestAborted;
            StringBuilder sb = new StringBuilder();
            
            while (!cancellationToken.IsCancellationRequested)
            {
                var readResult = await reader.ReadAsync(cancellationToken);
                var buffer = readResult.Buffer;
                var position = ReadItems(buffer, readResult.IsCompleted, ref sb);
                if (readResult.IsCompleted)
                {
                    reader.AdvanceTo(new SequencePosition());
                    break;
                }
                reader.AdvanceTo(position, buffer.End);
                
            }
            //  await reader.CompleteAsync();

            context.Items["Request"] = sb.ToString();
            _logger.LogInformation("Request Received from {Method} {URL}", request.Method, request.Host + request.PathBase + request.Path);
            context.Items.Remove("Request");
        }
        private static SequencePosition ReadItems(in ReadOnlySequence<byte> sequence, bool isCompleted, ref StringBuilder stringBuilder)
        {
            var reader = new SequenceReader<byte>(sequence);

            while (!reader.End) // loop until we've read the entire sequence
            {
                if (reader.TryReadTo(out ReadOnlySpan<byte> itemBytes, Comma, advancePastDelimiter: true)) // we have an item to handle
                {
                    var stringLine = Encoding.UTF8.GetString(itemBytes);
                    stringBuilder.Append(stringLine);
                }
                else if (isCompleted) // read last item which has no final delimiter
                {
                    var stringLine = ReadLastItem(sequence.Slice(reader.Position));
                    stringBuilder.Append(stringLine);
                    reader.Advance(sequence.Length); // advance reader to the end
                }
                else // no more items in this sequence
                {
                    break;
                }
            }

            return reader.Position;
        }

        private static string ReadLastItem(in ReadOnlySequence<byte> sequence)
        {
            var length = (int)sequence.Length;

            string stringLine;

            if (length < MaxStackLength) // if the item is small enough we'll stack allocate the buffer
            {
                Span<byte> byteBuffer = stackalloc byte[length];
                sequence.CopyTo(byteBuffer);
                stringLine = Encoding.UTF8.GetString(byteBuffer);
            }
            else // otherwise we'll rent an array to use as the buffer
            {
                var byteBuffer = ArrayPool<byte>.Shared.Rent(length);

                try
                {
                    sequence.CopyTo(byteBuffer);
                    stringLine = Encoding.UTF8.GetString(byteBuffer.AsSpan().Slice(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(byteBuffer);
                }
            }

            return stringLine;
        }


        // End of class
    }

    public static class MiddlewareExtensions
    {
        public static IApplicationBuilder UserLoggingMiddleware(
            this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<LoggingMiddleware>();
        }
    }
}
