using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace GenerationalApp
{
    class Program
    {
        private static ReadOnlySpan<byte> Delimiters => new[] { (byte)' ', (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> JunkChars => new[] { (byte)' ', (byte)'\r', (byte)'\n', (byte)'.', (byte)',', (byte)';', (byte)'!', (byte)'?', (byte)'"', (byte)':', (byte)'(', (byte)')', (byte)'_', (byte)'[', (byte)']' };
        static async Task Main(string[] args)
        {
            Trie<int> stringTrie = new();
            HttpClient client = new HttpClient();
            var url = "http://gutendex.com//books?languages=en&mime_type=text%2Fplain";
            int index = 0;
            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var mainTask = ctx.AddTask("[green]Processing books [/]");
                    int pageIndex = 1;
                    while (true)
                    {
                        HttpResponseMessage response = await client.GetAsync(url);
                        if (!response.IsSuccessStatusCode)
                            break;
                        var page = await response.Content.ReadFromJsonAsync<ResultsPage>();
                        if (page is null)
                            break;
                        mainTask.MaxValue = page.Count;

                        
                        ProgressTask pageTask = ctx.AddTask($"[darkgreen]Processing page {pageIndex:000}[/]");

                        foreach (var pageResult in page.Results)
                        {
                            pageTask.MaxValue = page.Results.Length;
                            if (pageResult.Formats.TryGetValue("text/plain; charset=utf-8", out var bookUrl) &&
                                bookUrl.EndsWith(".txt"))
                            {
                                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, new Uri(bookUrl, UriKind.Absolute));
                                HttpResponseMessage resp = await client.SendAsync(hrm);
                                Stream respStream = await resp.Content.ReadAsStreamAsync();
                                PipeReader reader = PipeReader.Create(respStream);

                                await ReadBookAsync(reader, stringTrie);

                                index++;

                                //System.InvalidOperationException: Could not find color or style 'Cambridge'.
                                AnsiConsole.WriteLine($"After parsing '{pageResult.Title}' trie size is {stringTrie.CountNodes()}");

                            }
                            pageTask.Value++;
                        }
                        pageIndex++;
                        mainTask.Value++;
                        if (page.Next is null)
                            break;
                        url = page.Next;
                    }
                });
           }

        static async ValueTask ReadBookAsync(PipeReader pReader, Trie<int> trie)
        {
            ReadResult result;
            do
            {
                result = await pReader.ReadAsync(CancellationToken.None);

                if (result.IsCanceled)
                {
                    throw new InvalidOperationException("Pipe cancelled 😪");
                }

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition sp = ReadBookCore(buffer, trie);

                pReader.AdvanceTo(sp, buffer.End);

            } while (!result.IsCompleted);
        }

        [SkipLocalsInit]
        static SequencePosition ReadBookCore(ReadOnlySequence<byte> buffer, Trie<int> trie)
        {
            Span<char> wordBuffer = stackalloc char[256];
            SequenceReader<byte> sReader = new SequenceReader<byte>(buffer);

            while(sReader.TryReadToAny(out ReadOnlySpan<byte> word, Delimiters, true))
            {
                if (word.IsEmpty)
                {
                    continue;
                }

                if (TryNormalize(word, wordBuffer, out string? wordStr))
                {
                    if (trie.TryGetItem(wordStr, out int counter))
                    {
                        ++counter;
                    }
                    trie.Add(wordStr, counter);
                }
            }

            return sReader.Position;
        }

        private static bool TryNormalize(ReadOnlySpan<byte> word, Span<char> buffer, [NotNullWhen(true)] out string? result)
        {
            word = word.Trim(JunkChars);
            if (word.IsEmpty)
            {
                goto FAILURE;
            }

            Span<char> remainingWordBuffer = buffer;
            ReadOnlySpan<byte> remaining = word;
            while (!remaining.IsEmpty)
            {
                OperationStatus status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
                if (status != OperationStatus.Done || !Rune.IsLetter(rune))
                {
                    goto FAILURE;
                }
                remaining = remaining.Slice(consumed);
                rune = Rune.ToLowerInvariant(rune);
                if (rune.TryEncodeToUtf16(remainingWordBuffer, out int charsWritten))
                {
                    remainingWordBuffer = remainingWordBuffer.Slice(charsWritten);
                }
                else
                {
                    goto FAILURE;
                }
            }

            result = new string(buffer.Slice(0, buffer.Length - remainingWordBuffer.Length));
            return true;

        FAILURE:
            result = null;
            return false;
        }
    }
}
