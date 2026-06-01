using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HttpMonitorApp;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, StoredMessage> _storedMessages = new();
    private readonly List<RequestLogEntry> _allLogs = [];
    private readonly object _logsLock = new();
    private readonly object _bucketLock = new();
    private readonly Dictionary<DateTime, int> _minuteBucketMap = [];
    private readonly Dictionary<DateTime, int> _hourBucketMap = [];
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private DateTime _serverStartedUtc;
    private string _methodFilter = "ALL";
    private string _statusFilter = "ALL";
    private string _bucketMode = "minute";
    private readonly string _logFilePath;

    private long _totalRequests;
    private long _getRequests;
    private long _postRequests;
    private long _totalProcessingMs;

    private string _portText = "8080";
    private string _serverStateText = "Сервер не запущен";
    private string _serverLogsText = string.Empty;
    private string _clientUrl = "https://jsonplaceholder.typicode.com/posts";
    private string _clientMethod = "POST";
    private string _clientRequestBody = "{\n  \"message\": \"Привет от клиента\"\n}";
    private string _clientResponseText = string.Empty;
    private long _totalRequestsUi;
    private long _getRequestsUi;
    private long _postRequestsUi;
    private double _averageProcessingMs;
    private int _maxBucketValue = 1;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _logFilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "logs.txt");
        Closing += MainWindow_Closing;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RequestLogEntry> VisibleLogs { get; } = [];
    public ObservableCollection<RequestBucket> CurrentBuckets { get; } = [];

    public string PortText
    {
        get => _portText;
        set => SetField(ref _portText, value);
    }

    public string ServerStateText
    {
        get => _serverStateText;
        set => SetField(ref _serverStateText, value);
    }

    public string ServerLogsText
    {
        get => _serverLogsText;
        set => SetField(ref _serverLogsText, value);
    }

    public string ClientUrl
    {
        get => _clientUrl;
        set => SetField(ref _clientUrl, value);
    }

    public string ClientMethod
    {
        get => _clientMethod;
        set => SetField(ref _clientMethod, value);
    }

    public string ClientRequestBody
    {
        get => _clientRequestBody;
        set => SetField(ref _clientRequestBody, value);
    }

    public string ClientResponseText
    {
        get => _clientResponseText;
        set => SetField(ref _clientResponseText, value);
    }

    public long TotalRequests
    {
        get => _totalRequestsUi;
        set => SetField(ref _totalRequestsUi, value);
    }

    public long GetRequests
    {
        get => _getRequestsUi;
        set => SetField(ref _getRequestsUi, value);
    }

    public long PostRequests
    {
        get => _postRequestsUi;
        set => SetField(ref _postRequestsUi, value);
    }

    public double AverageProcessingMs
    {
        get => _averageProcessingMs;
        set => SetField(ref _averageProcessingMs, value);
    }

    public int MaxBucketValue
    {
        get => _maxBucketValue;
        set => SetField(ref _maxBucketValue, value);
    }

    private async void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listener is not null && _listener.IsListening)
        {
            AppendServerTextLine("Сервер уже запущен.");
            return;
        }

        if (!int.TryParse(PortText, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Введите корректный порт (1-65535).", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить сервер: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            _listener = null;
            return;
        }

        _listenerCts = new CancellationTokenSource();
        _serverStartedUtc = DateTime.UtcNow;
        ServerStateText = $"Сервер запущен на http://localhost:{port}/";
        AppendServerTextLine(ServerStateText);
        _ = Task.Run(() => ListenLoopAsync(_listenerCts.Token));

        await Task.CompletedTask;
    }

    private async void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        await StopServerAsync();
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null && _listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var start = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var method = request.HttpMethod.ToUpperInvariant();
        var url = request.Url?.ToString() ?? string.Empty;
        var headersText = FormatHeaders(request.Headers);
        var requestBody = await ReadBodyAsync(request);

        var statusCode = 200;
        string responseBody;

        if (method == "GET")
        {
            Interlocked.Increment(ref _getRequests);
            responseBody = BuildServerStatusJson();
        }
        else if (method == "POST")
        {
            Interlocked.Increment(ref _postRequests);
            responseBody = HandlePostRequest(requestBody, out statusCode);
        }
        else
        {
            statusCode = 405;
            responseBody = JsonSerializer.Serialize(new { error = "Method not allowed. Use GET or POST." });
        }

        Interlocked.Increment(ref _totalRequests);

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;

        var bytes = Encoding.UTF8.GetBytes(responseBody);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();

        stopwatch.Stop();
        var elapsedMs = Math.Max(1, (long)stopwatch.Elapsed.TotalMilliseconds);
        Interlocked.Add(ref _totalProcessingMs, elapsedMs);
        RegisterBucket(start);
        UpdateStatsUi();

        var log = new RequestLogEntry
        {
            Timestamp = start.ToString("yyyy-MM-dd HH:mm:ss"),
            Direction = "Входящий",
            Method = method,
            Url = url,
            StatusCode = statusCode,
            DurationMs = elapsedMs,
            Headers = headersText,
            RequestBody = requestBody,
            ResponseBody = responseBody
        };

        await AddLogAsync(log);
    }

    private async void SendRequestButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ClientUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("Введите URL для запроса.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var method = (ClientMethod ?? "GET").Trim().ToUpperInvariant();
        var startedAt = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string requestBody = string.Empty;

        try
        {
            if (method == "GET")
            {
                response = await _httpClient.GetAsync(url);
            }
            else
            {
                requestBody = ClientRequestBody ?? string.Empty;
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(url, content);
            }

            var responseText = await response.Content.ReadAsStringAsync();
            ClientResponseText = $"{(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{Environment.NewLine}{responseText}";

            stopwatch.Stop();

            var log = new RequestLogEntry
            {
                Timestamp = startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Direction = "Исходящий",
                Method = method,
                Url = url,
                StatusCode = (int)response.StatusCode,
                DurationMs = Math.Max(1, (long)stopwatch.Elapsed.TotalMilliseconds),
                RequestBody = requestBody,
                ResponseBody = responseText,
                Headers = $"Content-Type: application/json; charset=utf-8"
            };

            await AddLogAsync(log);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ClientResponseText = $"Ошибка: {ex.Message}";

            var log = new RequestLogEntry
            {
                Timestamp = startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Direction = "Исходящий",
                Method = method,
                Url = url,
                StatusCode = 500,
                DurationMs = Math.Max(1, (long)stopwatch.Elapsed.TotalMilliseconds),
                RequestBody = requestBody,
                ResponseBody = ex.Message,
                Headers = "Client exception"
            };

            await AddLogAsync(log);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private void BucketModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedValue is null)
        {
            return;
        }

        _bucketMode = combo.SelectedValue.ToString() ?? "minute";
        RefreshBucketsUi();
    }

    private void MethodFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedValue is null)
        {
            return;
        }

        _methodFilter = combo.SelectedValue.ToString() ?? "ALL";
        ApplyFiltersUi();
    }

    private void StatusFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedValue is null)
        {
            return;
        }

        _statusFilter = combo.SelectedValue.ToString() ?? "ALL";
        ApplyFiltersUi();
    }

    private async Task StopServerAsync()
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            _listenerCts?.Cancel();
            _listener.Stop();
            _listener.Close();
            ServerStateText = "Сервер остановлен";
            AppendServerTextLine(ServerStateText);
        }
        catch (Exception ex)
        {
            AppendServerTextLine($"Ошибка при остановке сервера: {ex.Message}");
        }
        finally
        {
            _listener = null;
            _listenerCts?.Dispose();
            _listenerCts = null;
        }

        await Task.CompletedTask;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        await StopServerAsync();
        _httpClient.Dispose();
    }

    private string HandlePostRequest(string requestBody, out int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (!document.RootElement.TryGetProperty("message", out var messageElement))
            {
                statusCode = 400;
                return JsonSerializer.Serialize(new { error = "Missing 'message' field." });
            }

            var message = messageElement.GetString() ?? string.Empty;
            var id = Guid.NewGuid().ToString("N");
            _storedMessages[id] = new StoredMessage(id, message, DateTime.UtcNow);

            statusCode = 200;
            return JsonSerializer.Serialize(new { id, storedMessages = _storedMessages.Count });
        }
        catch (JsonException)
        {
            statusCode = 400;
            return JsonSerializer.Serialize(new { error = "Invalid JSON body." });
        }
    }

    private string BuildServerStatusJson()
    {
        var uptimeSeconds = _serverStartedUtc == default
            ? 0
            : (long)(DateTime.UtcNow - _serverStartedUtc).TotalSeconds;

        var payload = new
        {
            status = "running",
            uptimeSeconds,
            totalRequests = Interlocked.Read(ref _totalRequests),
            getRequests = Interlocked.Read(ref _getRequests),
            postRequests = Interlocked.Read(ref _postRequests),
            storedMessages = _storedMessages.Count
        };

        return JsonSerializer.Serialize(payload);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static string FormatHeaders(System.Collections.Specialized.NameValueCollection headers)
    {
        var lines = headers.AllKeys
            .Where(key => key is not null)
            .Select(key => $"{key}: {headers[key]}");

        return string.Join("; ", lines);
    }

    private async Task AddLogAsync(RequestLogEntry entry)
    {
        lock (_logsLock)
        {
            _allLogs.Add(entry);
        }

        var fileRecord = BuildFileLogRecord(entry);
        await AppendFileLogAsync(fileRecord);

        await Dispatcher.InvokeAsync(() =>
        {
            AppendServerTextLine(fileRecord);
            if (PassesFilters(entry))
            {
                VisibleLogs.Insert(0, entry);
            }
        });
    }

    private string BuildFileLogRecord(RequestLogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(entry.Timestamp).Append("] ");
        sb.Append(entry.Direction).Append(" | ");
        sb.Append(entry.Method).Append(' ').Append(entry.Url).Append(" | ");
        sb.Append("Status=").Append(entry.StatusCode).Append(" | ");
        sb.Append("Duration=").Append(entry.DurationMs).Append("ms");

        if (!string.IsNullOrWhiteSpace(entry.Headers))
        {
            sb.Append(" | Headers: ").Append(entry.Headers);
        }

        if (!string.IsNullOrWhiteSpace(entry.RequestBody))
        {
            sb.Append(" | RequestBody: ").Append(Shorten(entry.RequestBody, 200));
        }

        if (!string.IsNullOrWhiteSpace(entry.ResponseBody))
        {
            sb.Append(" | ResponseBody: ").Append(Shorten(entry.ResponseBody, 200));
        }

        return sb.ToString();
    }

    private async Task AppendFileLogAsync(string line)
    {
        await _fileLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void AppendServerTextLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendServerTextLine(line));
            return;
        }

        if (string.IsNullOrEmpty(ServerLogsText))
        {
            ServerLogsText = line;
            return;
        }

        ServerLogsText += Environment.NewLine + line;
    }

    private void UpdateStatsUi()
    {
        Dispatcher.Invoke(() =>
        {
            var total = Interlocked.Read(ref _totalRequests);
            var totalMs = Interlocked.Read(ref _totalProcessingMs);

            TotalRequests = total;
            GetRequests = Interlocked.Read(ref _getRequests);
            PostRequests = Interlocked.Read(ref _postRequests);
            AverageProcessingMs = total == 0 ? 0 : Math.Round((double)totalMs / total, 2);
        });
    }

    private void RegisterBucket(DateTime timestamp)
    {
        var minuteKey = new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0);
        var hourKey = new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0);

        lock (_bucketLock)
        {
            IncrementBucket(_minuteBucketMap, minuteKey, 180);
            IncrementBucket(_hourBucketMap, hourKey, 120);
        }

        Dispatcher.Invoke(RefreshBucketsUi);
    }

    private void RefreshBucketsUi()
    {
        List<RequestBucket> prepared;

        lock (_bucketLock)
        {
            var source = _bucketMode == "hour" ? _hourBucketMap : _minuteBucketMap;
            prepared = source
                .OrderBy(x => x.Key)
                .TakeLast(24)
                .Select(x => new RequestBucket
                {
                    Label = _bucketMode == "hour"
                        ? x.Key.ToString("MM-dd HH:00")
                        : x.Key.ToString("HH:mm"),
                    Count = x.Value
                })
                .ToList();
        }

        CurrentBuckets.Clear();
        foreach (var item in prepared)
        {
            CurrentBuckets.Add(item);
        }

        MaxBucketValue = Math.Max(1, prepared.Count == 0 ? 1 : prepared.Max(x => x.Count));
        DrawLoadChart();
    }

    private void LoadChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawLoadChart();
    }

    private void DrawLoadChart()
    {
        var canvas = LoadChartCanvas;
        if (canvas is null)
        {
            return;
        }

        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        const double leftMargin = 38;
        const double rightMargin = 12;
        const double topMargin = 12;
        const double bottomMargin = 30;

        var plotWidth = width - leftMargin - rightMargin;
        var plotHeight = height - topMargin - bottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        var buckets = CurrentBuckets.ToList();
        var maxValue = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(b => b.Count));

        var axisBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var gridBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3));
        var barBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4));
        var peakBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));

        var axisBottom = topMargin + plotHeight;

        // Оси
        canvas.Children.Add(new Line { X1 = leftMargin, Y1 = topMargin, X2 = leftMargin, Y2 = axisBottom, Stroke = axisBrush, StrokeThickness = 1 });
        canvas.Children.Add(new Line { X1 = leftMargin, Y1 = axisBottom, X2 = leftMargin + plotWidth, Y2 = axisBottom, Stroke = axisBrush, StrokeThickness = 1 });

        // Горизонтальная сетка и подписи оси Y
        const int yTicks = 4;
        for (var i = 0; i <= yTicks; i++)
        {
            var value = maxValue * i / (double)yTicks;
            var y = axisBottom - plotHeight * i / yTicks;

            if (i > 0)
            {
                canvas.Children.Add(new Line { X1 = leftMargin, Y1 = y, X2 = leftMargin + plotWidth, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            }

            var yLabel = new TextBlock { Text = Math.Round(value).ToString(), FontSize = 10, Foreground = axisBrush };
            Canvas.SetLeft(yLabel, 4);
            Canvas.SetTop(yLabel, y - 8);
            canvas.Children.Add(yLabel);
        }

        if (buckets.Count == 0)
        {
            var empty = new TextBlock { Text = "Нет данных о нагрузке", FontSize = 12, Foreground = axisBrush };
            Canvas.SetLeft(empty, leftMargin + plotWidth / 2 - 60);
            Canvas.SetTop(empty, topMargin + plotHeight / 2 - 8);
            canvas.Children.Add(empty);
            return;
        }

        var slot = plotWidth / buckets.Count;
        var barWidth = Math.Max(2, slot * 0.6);
        var labelStep = Math.Max(1, (int)Math.Ceiling(buckets.Count / (plotWidth / 55.0)));

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var barHeight = plotHeight * bucket.Count / maxValue;
            var x = leftMargin + slot * i + (slot - barWidth) / 2;
            var y = axisBottom - barHeight;

            var rect = new Rectangle
            {
                Width = barWidth,
                Height = Math.Max(0, barHeight),
                Fill = bucket.Count == maxValue ? peakBrush : barBrush,
                ToolTip = $"{bucket.Label}: {bucket.Count}"
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);

            // Значение над пиковым столбцом
            if (bucket.Count == maxValue && bucket.Count > 0)
            {
                var valueLabel = new TextBlock { Text = bucket.Count.ToString(), FontSize = 9, Foreground = peakBrush };
                Canvas.SetLeft(valueLabel, x);
                Canvas.SetTop(valueLabel, Math.Max(topMargin, y - 13));
                canvas.Children.Add(valueLabel);
            }

            // Подписи оси X (с прореживанием)
            if (i % labelStep == 0)
            {
                var xLabel = new TextBlock
                {
                    Text = bucket.Label,
                    FontSize = 9,
                    Foreground = axisBrush,
                    RenderTransform = new RotateTransform(35),
                    RenderTransformOrigin = new Point(0, 0)
                };
                Canvas.SetLeft(xLabel, leftMargin + slot * i + slot / 2);
                Canvas.SetTop(xLabel, axisBottom + 4);
                canvas.Children.Add(xLabel);
            }
        }
    }

    private static void IncrementBucket(IDictionary<DateTime, int> map, DateTime key, int maxItems)
    {
        if (!map.TryAdd(key, 1))
        {
            map[key]++;
        }

        if (map.Count <= maxItems)
        {
            return;
        }

        var oldest = map.Keys.Min();
        map.Remove(oldest);
    }

    private void ApplyFiltersUi()
    {
        List<RequestLogEntry> copy;
        lock (_logsLock)
        {
            copy = _allLogs.ToList();
        }

        var filtered = copy
            .Where(PassesFilters)
            .OrderByDescending(x => x.Timestamp)
            .ToList();

        VisibleLogs.Clear();
        foreach (var entry in filtered)
        {
            VisibleLogs.Add(entry);
        }
    }

    private bool PassesFilters(RequestLogEntry entry)
    {
        var methodOk = _methodFilter == "ALL" || string.Equals(entry.Method, _methodFilter, StringComparison.OrdinalIgnoreCase);
        var statusOk = _statusFilter switch
        {
            "2xx" => entry.StatusCode is >= 200 and < 300,
            "4xx" => entry.StatusCode is >= 400 and < 500,
            "5xx" => entry.StatusCode is >= 500 and < 600,
            _ => true
        };

        return methodOk && statusOk;
    }

    internal static string Shorten(string value, int maxLen)
    {
        var oneLine = value.Replace(Environment.NewLine, " ").Replace('\n', ' ').Replace('\r', ' ');
        return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "...";
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName ?? string.Empty);
        return true;
    }
}

public sealed class RequestLogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string Headers { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;

    public string RequestBodyPreview => MainWindow.Shorten(RequestBody, 80);
    public string ResponseBodyPreview => MainWindow.Shorten(ResponseBody, 80);
}

public sealed class RequestBucket
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed record StoredMessage(string Id, string Message, DateTime CreatedAtUtc);
