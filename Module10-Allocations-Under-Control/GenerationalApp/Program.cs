using System;
using System.Buffers;
using System.Diagnostics;
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
                    while (true)
                    {
                        HttpResponseMessage response = await client.GetAsync(url);
                        if (!response.IsSuccessStatusCode)
                            break;
                        var page = await response.Content.ReadFromJsonAsync<ResultsPage>();
                        if (page is null)
                            break;
                        mainTask.MaxValue = page.Count;

                        int pageIndex = 1;
                        ProgressTask pageTask = ctx.AddTask($"[darkgreen]Processing page {pageIndex}[/]");

                        foreach (var pageResult in page.Results)
                        {
                            pageTask.MaxValue = page.Results.Length;
                            if (pageResult.Formats.TryGetValue("text/plain; charset=utf-8", out var bookUrl) &&
                                bookUrl.EndsWith(".txt"))
                            {
                                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Get, new Uri(bookUrl, UriKind.Absolute));
                                HttpResponseMessage resp = await client.SendAsync(hrm);
                                Task<Stream> respStreamTask = resp.Content.ReadAsStreamAsync();
                                    Stream respStream = await respStreamTask;
                                PipeReader reader = PipeReader.Create(respStream);
                                
                                await ReadBookAsync(reader, stringTrie);


                                index++;
                                mainTask.Value = index;
                                pageTask.Value++;
                                //System.InvalidOperationException: Could not find color or style 'Cambridge'.
                                AnsiConsole.WriteLine($"After parsing '{pageResult.Title}' trie size is {stringTrie.CountNodes()}");

                            }
                            pageIndex++;
                        }
                        if (page.Next is null)
                            break;
                        url = page.Next;
                    }
                });
           }

        
        static (SequencePosition, long) ReadBookCore(ReadOnlySequence<byte> buffer, Trie<int> trie)
        {
            SequenceReader<byte> sReader = new SequenceReader<byte>(buffer);
            while(sReader.TryReadToAny(out ReadOnlySpan<byte> word, Delimiters, true))
            {
                if (word.IsEmpty)
                {
                    continue;
                }

                if (TryNormalize(word, out string wordStr))
                {
                    var newValue = 0;
                    if (trie.TryGetItem(wordStr, out var counter))
                        newValue = ++counter;
                    trie.Add(wordStr, newValue);
                }
            }

            return (sReader.Position, sReader.Consumed);
        }

        static async ValueTask ReadBookAsync(PipeReader pReader, Trie<int> trie)
        {
            ReadResult result;
            do
            {
                result = await pReader.ReadAsync(CancellationToken.None);

                if (result.IsCanceled) throw new InvalidOperationException("Pipe cancelled 😪");

                ReadOnlySequence<byte> buffer = result.Buffer;
                (SequencePosition sp, long consumed) = ReadBookCore(buffer, trie);

                if (!sp.Equals(result.Buffer.GetPosition(consumed)))
                {
                    throw new InvalidOperationException("unexpected consumption");
                }
                
                pReader.AdvanceTo(sp, buffer.End);

            } while (!result.IsCompleted);
        }

        private static bool TryNormalize(ReadOnlySpan<byte> word, out string result)
        {
            word = word.Trim(JunkChars);

            // This hurts but implementing UTF-8 strings is just out of scope
            result = Encoding.UTF8.GetString(word).ToLowerInvariant();


            for(int idx = 0; idx < result.Length; idx++)
            {
                if (!char.IsLetter(result[idx]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
