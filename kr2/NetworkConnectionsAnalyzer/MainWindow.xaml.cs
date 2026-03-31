using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NetworkConnectionsAnalyzer.Models;

namespace NetworkConnectionsAnalyzer;

public partial class MainWindow : Window
{
    private const int MaxHistoryItems = 50;

    private readonly ObservableCollection<NetworkInterfaceViewModel> _interfaces = [];
    private readonly ObservableCollection<string> _history = [];
    private readonly string _historyFilePath;

    public MainWindow()
    {
        InitializeComponent();

        InterfacesListBox.ItemsSource = _interfaces;
        HistoryListBox.ItemsSource = _history;

        _historyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetworkConnectionsAnalyzer",
            "history.json");

        LoadInterfaces();
        LoadHistory();

        if (_interfaces.Count > 0)
        {
            InterfacesListBox.SelectedIndex = 0;
        }
    }

    private void LoadInterfaces()
    {
        _interfaces.Clear();

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties ipProps = networkInterface.GetIPProperties();
            UnicastIPAddressInformation? ipv4 = ipProps.UnicastAddresses
                .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork);

            string ipAddress = ipv4?.Address.ToString() ?? "-";
            string subnetMask = ipv4?.IPv4Mask?.ToString() ?? "-";

            string macAddress = string.Join(
                "-",
                networkInterface.GetPhysicalAddress()
                    .GetAddressBytes()
                    .Select(b => b.ToString("X2")));

            _interfaces.Add(new NetworkInterfaceViewModel
            {
                Name = networkInterface.Name,
                Description = networkInterface.Description,
                IpAddress = ipAddress,
                SubnetMask = subnetMask,
                MacAddress = string.IsNullOrWhiteSpace(macAddress) ? "-" : macAddress,
                Status = networkInterface.OperationalStatus.ToString(),
                SpeedMbps = networkInterface.Speed > 0 ? $"{networkInterface.Speed / 1_000_000} Мбит/с" : "-",
                InterfaceType = networkInterface.NetworkInterfaceType.ToString()
            });
        }
    }

    private async void AnalyzeUrlButton_Click(object sender, RoutedEventArgs e)
    {
        string input = UrlTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(input))
        {
            OutputTextBox.Text = "Введите URL для анализа.";
            return;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
        {
            OutputTextBox.Text = "Некорректный URL. Пример: https://example.com/path?x=1#part";
            return;
        }

        StringBuilder sb = new();

        sb.AppendLine("Компоненты URL:");
        sb.AppendLine($"Схема: {uri.Scheme}");
        sb.AppendLine($"Хост: {uri.Host}");
        sb.AppendLine($"Порт: {(uri.IsDefaultPort ? "по умолчанию" : uri.Port)}");
        sb.AppendLine($"Путь: {uri.AbsolutePath}");
        sb.AppendLine($"Параметры запроса: {FormatUriPart(uri.Query)}");
        sb.AppendLine($"Фрагмент: {FormatUriPart(uri.Fragment)}");
        sb.AppendLine();

        sb.AppendLine("Проверка доступности хоста (ping):");
        sb.AppendLine(await GetPingInfoAsync(uri.Host));
        sb.AppendLine();

        sb.AppendLine("DNS-информация:");
        sb.AppendLine(await GetDnsInfoAsync(uri.Host));

        OutputTextBox.Text = sb.ToString();

        AddToHistory(uri.AbsoluteUri);
        await SaveHistoryAsync();
    }

    private void InterfacesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InterfacesListBox.SelectedItem is not NetworkInterfaceViewModel selected)
        {
            InterfaceDetailsTextBox.Text = string.Empty;
            return;
        }

        StringBuilder sb = new();
        sb.AppendLine($"Название: {selected.Name}");
        sb.AppendLine($"Описание: {selected.Description}");
        sb.AppendLine($"IP-адрес: {selected.IpAddress}");
        sb.AppendLine($"Маска подсети: {selected.SubnetMask}");
        sb.AppendLine($"MAC-адрес: {selected.MacAddress}");
        sb.AppendLine($"Состояние: {selected.Status}");
        sb.AppendLine($"Скорость: {selected.SpeedMbps}");
        sb.AppendLine($"Тип интерфейса: {selected.InterfaceType}");

        InterfaceDetailsTextBox.Text = sb.ToString();
    }

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is string item)
        {
            UrlTextBox.Text = item;
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();

        if (File.Exists(_historyFilePath))
        {
            File.Delete(_historyFilePath);
        }

        await SaveHistoryAsync();
    }

    private static string FormatUriPart(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static async Task<string> GetPingInfoAsync(string host)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(host, 2000);

            if (reply.Status == IPStatus.Success)
            {
                return $"Хост доступен. Время: {reply.RoundtripTime} мс, адрес: {reply.Address}";
            }

            return $"Хост недоступен. Статус: {reply.Status}";
        }
        catch (Exception ex)
        {
            return $"Ошибка ping: {ex.Message}";
        }
    }

    private static async Task<string> GetDnsInfoAsync(string host)
    {
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);

            if (addresses.Length == 0)
            {
                return "Адреса не найдены.";
            }

            StringBuilder sb = new();
            foreach (IPAddress address in addresses)
            {
                sb.AppendLine($"{address} ({DetermineAddressType(address)})");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Ошибка DNS: {ex.Message}";
        }
    }

    private static string DetermineAddressType(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return "loopback";
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();

            bool isPrivate =
                bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254);

            return isPrivate ? "локальный" : "публичный";
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return "локальный";
            }

            byte firstByte = address.GetAddressBytes()[0];
            bool isUniqueLocal = firstByte is 0xFC or 0xFD;

            return isUniqueLocal ? "локальный" : "публичный";
        }

        return "неизвестный";
    }

    private void AddToHistory(string url)
    {
        int existingIndex = _history.IndexOf(url);

        if (existingIndex >= 0)
        {
            _history.RemoveAt(existingIndex);
        }

        _history.Insert(0, url);

        while (_history.Count > MaxHistoryItems)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return;
            }

            string content = File.ReadAllText(_historyFilePath);
            List<string>? items = JsonSerializer.Deserialize<List<string>>(content);

            if (items is null)
            {
                return;
            }

            foreach (string item in items)
            {
                _history.Add(item);
            }
        }
        catch
        {
            // tipo ignor chtobi ne lomalos
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            string directory = Path.GetDirectoryName(_historyFilePath)!;
            Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(_history);
            await File.WriteAllTextAsync(_historyFilePath, json);
        }
        catch
        {
            // tut toje ignor chtobi ne lomalos
        }
    }
}
