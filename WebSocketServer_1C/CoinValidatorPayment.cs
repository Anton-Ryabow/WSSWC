using dk.CctalkLib.Connections;
using dk.CctalkLib.Devices;
using System.Text;
using System.Text.Json;
using System.Timers;
using WebSocketServer_1C;

public class CoinValidatorPayment : PaymentBase
{
    private bool _isClosed;
    private bool _isErrorHandled;
    private volatile bool _isDisposed = false;

    private CoinAcceptor _coinAcceptor;
    private System.Timers.Timer _pollingTimer;
    private Dictionary<byte, CoinTypeInfo> _coinConfig;

    public CoinValidatorPayment(string portName)
    {
        InitializeCoinAcceptor(portName);
        StartPolling();
    }

    private void InitializeCoinAcceptor(string portName)
    {
        var connection = new ConnectionRs232
        {
            PortName = portName,
        };

        _coinConfig = CreateCustomCoinConfig();
        _coinAcceptor = new CoinAcceptor(Convert.ToByte(0), connection, _coinConfig, null);
        _coinAcceptor.CoinAccepted += OnCoinAccepted;
        _coinAcceptor.ErrorMessageAccepted += OnErrorOccurred;
        try
        {
            _coinAcceptor.Init();
        }
        catch (Exception ex)
        {
            _coinAcceptor.Dispose();
            throw;
        }

        _coinAcceptor.IsInhibiting = false;

        FileLogger.Log("[COIN] Created");
    }

    private Dictionary<byte, CoinTypeInfo> CreateCustomCoinConfig()
    {
        //var coins = new Dictionary<byte, CoinTypeInfo>();

        //coins.Add(0x0A, new CoinTypeInfo("1 RUB", 1.0m));
        //coins.Add(0x0C, new CoinTypeInfo("2 RUB", 2.0m));
        //coins.Add(0x0E, new CoinTypeInfo("5 RUB", 5.0m));
        //coins.Add(0x10, new CoinTypeInfo("10 RUB", 10.0m));

        //return coins;

        var coins = new Dictionary<byte, CoinTypeInfo>();

        string json = File.ReadAllText("CoinsConfig.json");

        var config = JsonSerializer.Deserialize<Dictionary<string, int>>(json);

        foreach (var kvp in config)
        {
            string key = kvp.Key; 
            int channel = kvp.Value;

            byte channelByte = (byte)channel;

            string name = key.Replace("R", " RUB");
            decimal value = decimal.Parse(key.Replace("R", ""));

            coins.Add(channelByte, new CoinTypeInfo(name, value));
        }

        return coins;
    }

    private void StartPolling()
    {
        _pollingTimer = new System.Timers.Timer(1000);
        _pollingTimer.Elapsed += PollDevice;
        _pollingTimer.Start();
    }

    private void StopPolling()
    {
        _pollingTimer.Stop();
        _pollingTimer.Elapsed -= PollDevice;
        _pollingTimer.Dispose();
    }

    private void PollDevice(object? sender, ElapsedEventArgs e)
    {
        try
        {
            if (_isDisposed) return;

            _coinAcceptor?.PollNow();
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;

            _isDisposed = true;
            StopPolling();

            _coinAcceptor.CoinAccepted -= OnCoinAccepted;
            _coinAcceptor.ErrorMessageAccepted -= OnErrorOccurred;

            try
            {
                _coinAcceptor.UnInit();
            }
            catch { /* ignore */ }

            _coinAcceptor?.Dispose();

            FileLogger.Log($"[COIN][Error] Polling: {ex.Message}");

            if ((ex.Message == "Device not respondng" || ex.Message.StartsWith("Pause in reply")) && !_isErrorHandled)
            {
                _isErrorHandled = true;
                _isClosed = true;
                OnError();
            }
        }
    }

    private void OnCoinAccepted(object sender, CoinAcceptorCoinEventArgs e)
    {
        FileLogger.Log("[COIN] Accepted " + e.CoinValue);
        FileLogger.Log("[COIN] Accepted code: " + e.CoinCode);
        OnPartResult((int)e.CoinValue);
    }

    private void OnErrorOccurred(object sender, CoinAcceptorErrorEventArgs e)
    {
        FileLogger.Log($"[COIN][ERROR] By CoinValidator: {e.ErrorMessage}");
        OnError();
    }

    public override void CloseSession()
    {
        if (_isClosed || _isDisposed) return;

        _isDisposed = true;
        StopPolling();

        try
        {
            _coinAcceptor.IsInhibiting = true;
            _coinAcceptor.CoinAccepted -= OnCoinAccepted;
            _coinAcceptor.ErrorMessageAccepted -= OnErrorOccurred;

            _coinAcceptor.UnInit();
            _coinAcceptor.Dispose();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[COIN][ERROR] During close: {ex.Message}");
        }

        _isClosed = true;
        FileLogger.Log("[COIN] Closed");
    }
}