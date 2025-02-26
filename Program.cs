using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Threading;

class Program
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" } }
    };
    private static readonly HashSet<string> _visitedUrls = new HashSet<string>();
    private static readonly List<(string Url, string Referrer)> _notFoundUrls = new List<(string, string)>();
    private static string? _baseUrl;
    private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(10);
    private static readonly Queue<(string Url, string Referrer)> _urlQueue = new Queue<(string, string)>();

    static async Task ProcessQueue()
    {
        var tasks = new List<Task>();
        var maxConcurrentTasks = 10;

        while (_urlQueue.Count > 0 || tasks.Count > 0)
        {
            // Start new tasks if we have URLs and are below max concurrent tasks
            while (_urlQueue.Count > 0 && tasks.Count < maxConcurrentTasks)
            {
                var (url, referrer) = _urlQueue.Dequeue();
                if (!_visitedUrls.Contains(url))
                {
                    tasks.Add(ProcessUrl(url, referrer));
                }
            }

            if (tasks.Count > 0)
            {
                // Wait for at least one task to complete
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                await completed; // Propagate any exceptions
            }
        }
    }

    static async Task ProcessUrl(string url, string referrer)
    {
        if (!_visitedUrls.Add(url)) return; // Thread-safe check-and-set
        Console.WriteLine($"Checking: {url}");

        try
        {
            await _semaphore.WaitAsync();
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    lock (_notFoundUrls)
                    {
                        _notFoundUrls.Add((url, referrer));
                    }
                    Console.WriteLine($"404 Error found: {url} (linked from {referrer})");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                    return;

                var finalUrl = response.RequestMessage?.RequestUri?.ToString();
                if (finalUrl == null) return;

                if (_baseUrl == null || !finalUrl.StartsWith(_baseUrl))
                    return;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                var html = await reader.ReadToEndAsync();

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var links = doc.DocumentNode.SelectNodes("//a[@href]");
                if (links == null) return;

                foreach (var link in links)
                {
                    var href = link.GetAttributeValue("href", string.Empty);
                    if (string.IsNullOrEmpty(href)) continue;

                    var absoluteUrl = ResolveUrl(finalUrl, href);
                    if (absoluteUrl == null) continue;

                    lock (_urlQueue)
                    {
                        _urlQueue.Enqueue((absoluteUrl, finalUrl));
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing {url}: {ex.Message}");
        }
    }

    static async Task Main(string[] args)
    {
        try
        {
            Console.WriteLine("Enter the website URL to crawl (e.g., example.com or https://example.com):");
            var inputUrl = Console.ReadLine()?.TrimEnd('/');

            if (string.IsNullOrEmpty(inputUrl))
            {
                Console.WriteLine("Invalid URL");
                return;
            }

            if (!inputUrl.StartsWith("http://") && !inputUrl.StartsWith("https://"))
            {
                inputUrl = "https://" + inputUrl;
            }

            _baseUrl = inputUrl;
            _urlQueue.Enqueue((_baseUrl, "Initial URL"));
            await ProcessQueue();

            var results = _notFoundUrls.Select(x => $"{x.Url} (linked from {x.Referrer})");
            await File.WriteAllLinesAsync("404_errors.txt", results);
            Console.WriteLine($"\nCrawling completed. Found {_notFoundUrls.Count} 404 errors.");
            Console.WriteLine("Results have been saved to 404_errors.txt");
        }
        finally
        {
            _semaphore.Dispose();
        }
    }

    static string? ResolveUrl(string baseUrl, string href)
    {
        try
        {
            // Skip javascript: links, mailto:, tel:, etc.
            if (href.Contains(":") && !href.StartsWith("http"))
                return null;

            // Handle anchor links
            if (href.StartsWith("#"))
                return null;

            // Handle absolute URLs (including http:// and https://)
            if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absoluteUri))
            {
                // Only process http/https schemes and upgrade to https
                if (absoluteUri.Scheme != "http" && absoluteUri.Scheme != "https")
                    return null;
                return "https://" + absoluteUri.Host + absoluteUri.PathAndQuery + absoluteUri.Fragment;
            }

            // Handle protocol-relative URLs that start with //
            if (href.StartsWith("//"))
                return "https:" + href;

            // Convert relative URLs to absolute
            if (href.StartsWith("/"))
            {
                if (_baseUrl == null) return null;
                var baseUri = new Uri(_baseUrl);
                return $"https://{baseUri.Host}{href}";
            }

            // Handle relative URLs without leading slash
            var fullUri = new Uri(new Uri(baseUrl), href);
            if (fullUri.Scheme != "http" && fullUri.Scheme != "https")
                return null;
            return "https://" + fullUri.Host + fullUri.PathAndQuery + fullUri.Fragment;
        }
        catch
        {
            return null;
        }
    }
} 