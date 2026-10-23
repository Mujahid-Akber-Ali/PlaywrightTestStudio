using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Opens a link and reads back how it performed: the load timeline the browser
/// itself recorded (DNS, connect, first byte, DOM ready, load), the paint and
/// web-vital milestones a user actually feels, how many bytes came down, and
/// which requests took the longest.
///
/// Everything here comes from the page's own Performance API, the same numbers
/// the browser's dev tools show, so there is nothing to calibrate and nothing
/// that depends on the machine the tool happens to run on.
/// </summary>
internal static class Performance
{
    public static async Task<PerfReport> MeasureAsync(string url, bool headless, Action<string>? progress = null)
    {
        await using var session = await BrowserSession.StartAsync(headless);
        var page = session.Page;

        // Largest-contentful-paint and layout-shift only arrive through an
        // observer, and only one that was listening before the page painted.
        // Installed as an init script, it is in place for the navigation below.
        await page.AddInitScriptAsync(VitalsScript);

        progress?.Invoke($"Opening {url}");

        var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        // The load event is only the start of the interesting part: lazy images,
        // the largest paint and late layout shifts all land after it. A quiet
        // network is a good enough signal that the page has settled.
        progress?.Invoke("Letting the page settle...");

        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 6_000 });
        }
        catch (TimeoutException)
        {
            // A page that keeps polling never goes idle; what we have is enough.
        }

        await page.WaitForTimeoutAsync(500);

        progress?.Invoke("Reading the metrics...");

        var raw = JsonSerializer.Deserialize<Raw>(
            await page.EvaluateAsync<string>(CollectScript), Storage.Json) ?? new Raw();

        return Build(url, response?.Status ?? 0, raw);
    }

    // -----------------------------------------------------------------------
    // Shaping the raw page numbers into a labelled report
    // -----------------------------------------------------------------------

    private static PerfReport Build(string url, int status, Raw raw)
    {
        var report = new PerfReport
        {
            Url = url,
            HttpStatus = status,
            CapturedUtc = DateTime.UtcNow
        };

        var t = raw.Timing;

        // The load timeline, in the order the browser walks it.
        report.Timeline.Add(new PerfMetric("DNS lookup", t.Dns, "ms"));
        report.Timeline.Add(new PerfMetric("TCP connect", t.Tcp, "ms"));
        report.Timeline.Add(new PerfMetric("TLS handshake", t.Tls, "ms"));
        report.Timeline.Add(new PerfMetric("Time to first byte", t.Ttfb, "ms"));
        report.Timeline.Add(new PerfMetric("Response download", t.ResponseDownload, "ms"));
        report.Timeline.Add(new PerfMetric("DOM interactive", t.DomInteractive, "ms"));
        report.Timeline.Add(new PerfMetric("DOM content loaded", t.DomContentLoaded, "ms"));
        report.Timeline.Add(new PerfMetric("Page load", t.Load, "ms"));

        report.Experience.Add(new PerfMetric("First paint", raw.Paint.FirstPaint, "ms"));
        report.Experience.Add(new PerfMetric("First contentful paint", raw.Paint.FirstContentfulPaint, "ms"));
        report.Experience.Add(new PerfMetric("Largest contentful paint", raw.Vitals.LargestContentfulPaint, "ms"));
        report.Experience.Add(new PerfMetric("Cumulative layout shift", raw.Vitals.CumulativeLayoutShift, "score"));

        report.Transfer.Add(new PerfMetric("Document transferred", raw.Transfer.TransferSize, "bytes"));
        report.Transfer.Add(new PerfMetric("Document uncompressed", raw.Transfer.DecodedBodySize, "bytes"));
        report.Transfer.Add(new PerfMetric("Requests (after document)", raw.Resources.Total, "count"));
        report.Transfer.Add(new PerfMetric("Resources transferred", raw.Resources.TotalSize, "bytes"));

        foreach (var pair in raw.Resources.ByType.OrderByDescending(p => p.Value.Size))
        {
            report.ByType.Add(new PerfResourceGroup(pair.Key, pair.Value.Count, pair.Value.Size,
                Math.Round(pair.Value.Duration, 1)));
        }

        foreach (var r in raw.Resources.Slowest)
        {
            report.Slowest.Add(new PerfResource(r.Name, r.Type, Math.Round(r.Duration, 1), r.Size));
        }

        return report;
    }

    /// <summary>
    /// Installed before navigation so the observers are listening when the first
    /// paint happens. Written as plain statements, not an arrow, because an init
    /// script is run as-is in the page rather than called as a function.
    /// </summary>
    private const string VitalsScript = """
    window.__ptsVitals = { lcp: 0, cls: 0 };
    try {
      new PerformanceObserver(function (list) {
        var entries = list.getEntries();
        var last = entries[entries.length - 1];
        if (last) { window.__ptsVitals.lcp = last.renderTime || last.startTime || 0; }
      }).observe({ type: 'largest-contentful-paint', buffered: true });
    } catch (e) { }
    try {
      new PerformanceObserver(function (list) {
        list.getEntries().forEach(function (e) {
          if (!e.hadRecentInput) { window.__ptsVitals.cls += e.value; }
        });
      }).observe({ type: 'layout-shift', buffered: true });
    } catch (e) { }
    """;

    /// <summary>
    /// Runs in the page and returns one JSON string holding everything the
    /// Performance API knows about this load.
    /// </summary>
    private const string CollectScript = """
    () => {
      const nav = performance.getEntriesByType('navigation')[0] || {};
      const ms = (v) => Math.max(0, Math.round(v || 0));

      const paints = {};
      performance.getEntriesByType('paint').forEach(p => { paints[p.name] = p.startTime; });

      const resources = performance.getEntriesByType('resource');
      const byType = {};
      resources.forEach(r => {
        const t = r.initiatorType || 'other';
        if (!byType[t]) byType[t] = { count: 0, size: 0, duration: 0 };
        byType[t].count += 1;
        byType[t].size += r.transferSize || 0;
        byType[t].duration += r.duration || 0;
      });

      const slowest = resources.slice()
        .sort((a, b) => (b.duration || 0) - (a.duration || 0))
        .slice(0, 15)
        .map(r => ({
          name: r.name,
          type: r.initiatorType || 'other',
          duration: r.duration || 0,
          size: r.transferSize || 0
        }));

      const vitals = window.__ptsVitals || {};

      return JSON.stringify({
        timing: {
          dns: ms(nav.domainLookupEnd - nav.domainLookupStart),
          tcp: ms(nav.connectEnd - nav.connectStart),
          tls: nav.secureConnectionStart > 0 ? ms(nav.connectEnd - nav.secureConnectionStart) : 0,
          ttfb: ms(nav.responseStart),
          responseDownload: ms(nav.responseEnd - nav.responseStart),
          domInteractive: ms(nav.domInteractive),
          domContentLoaded: ms(nav.domContentLoadedEventEnd),
          load: ms(nav.loadEventEnd)
        },
        paint: {
          firstPaint: ms(paints['first-paint']),
          firstContentfulPaint: ms(paints['first-contentful-paint'])
        },
        vitals: {
          largestContentfulPaint: ms(vitals.lcp),
          cumulativeLayoutShift: Math.round((vitals.cls || 0) * 1000) / 1000
        },
        transfer: {
          transferSize: nav.transferSize || 0,
          encodedBodySize: nav.encodedBodySize || 0,
          decodedBodySize: nav.decodedBodySize || 0
        },
        resources: {
          total: resources.length,
          totalSize: resources.reduce((s, r) => s + (r.transferSize || 0), 0),
          byType: byType,
          slowest: slowest
        }
      });
    }
    """;

    // -----------------------------------------------------------------------
    // The shape the collect script returns
    // -----------------------------------------------------------------------

    private sealed class Raw
    {
        public RawTiming Timing { get; set; } = new();
        public RawPaint Paint { get; set; } = new();
        public RawVitals Vitals { get; set; } = new();
        public RawTransfer Transfer { get; set; } = new();
        public RawResources Resources { get; set; } = new();
    }

    private sealed class RawTiming
    {
        public double Dns { get; set; }
        public double Tcp { get; set; }
        public double Tls { get; set; }
        public double Ttfb { get; set; }
        public double ResponseDownload { get; set; }
        public double DomInteractive { get; set; }
        public double DomContentLoaded { get; set; }
        public double Load { get; set; }
    }

    private sealed class RawPaint
    {
        public double FirstPaint { get; set; }
        public double FirstContentfulPaint { get; set; }
    }

    private sealed class RawVitals
    {
        public double LargestContentfulPaint { get; set; }
        public double CumulativeLayoutShift { get; set; }
    }

    private sealed class RawTransfer
    {
        public long TransferSize { get; set; }
        public long EncodedBodySize { get; set; }
        public long DecodedBodySize { get; set; }
    }

    private sealed class RawResources
    {
        public int Total { get; set; }
        public long TotalSize { get; set; }
        public Dictionary<string, RawGroup> ByType { get; set; } = new();
        public List<RawResource> Slowest { get; set; } = new();
    }

    private sealed class RawGroup
    {
        public int Count { get; set; }
        public long Size { get; set; }
        public double Duration { get; set; }
    }

    private sealed class RawResource
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public double Duration { get; set; }
        public long Size { get; set; }
    }
}

/// <summary>How one link performed, ready to be written out or summarised.</summary>
internal sealed class PerfReport
{
    public string Url { get; set; } = "";
    public int HttpStatus { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The load timeline the browser recorded, in order.</summary>
    public List<PerfMetric> Timeline { get; } = new();

    /// <summary>Paint and web-vital milestones a person actually feels.</summary>
    public List<PerfMetric> Experience { get; } = new();

    /// <summary>How much came down the wire.</summary>
    public List<PerfMetric> Transfer { get; } = new();

    /// <summary>Requests after the document, grouped by what asked for them.</summary>
    public List<PerfResourceGroup> ByType { get; } = new();

    /// <summary>The slowest individual requests, longest first.</summary>
    public List<PerfResource> Slowest { get; } = new();

    [JsonIgnore]
    public string Host
    {
        get
        {
            try
            {
                return new Uri(Url).Host;
            }
            catch (Exception)
            {
                return Url;
            }
        }
    }

    /// <summary>A one-line headline for a confirmation prompt.</summary>
    public string Headline()
    {
        double Of(IEnumerable<PerfMetric> group, string label) =>
            group.FirstOrDefault(m => m.Label == label)?.Value ?? 0;

        var load = Of(Timeline, "Page load");
        var fcp = Of(Experience, "First contentful paint");
        var lcp = Of(Experience, "Largest contentful paint");

        return $"Page load {load / 1000:0.00}s    -    first contentful paint {fcp / 1000:0.00}s" +
               (lcp > 0 ? $"    -    largest paint {lcp / 1000:0.00}s" : "") +
               $"\r\n{ByType.Sum(g => g.Count) + 1} request(s), " +
               $"{PerfBook.Bytes(Transfer.FirstOrDefault(m => m.Label == "Resources transferred")?.Value ?? 0)} transferred.";
    }
}

/// <summary>A single measurement: what it is, its value, and the unit it is in.</summary>
internal sealed record PerfMetric(string Label, double Value, string Unit);

/// <summary>The requests of one kind (script, css, image...) taken together.</summary>
internal sealed record PerfResourceGroup(string Type, int Count, long Bytes, double TotalMs);

/// <summary>One request and how it did.</summary>
internal sealed record PerfResource(string Name, string Type, double DurationMs, long Bytes);
