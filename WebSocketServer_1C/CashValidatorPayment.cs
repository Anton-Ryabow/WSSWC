using CashCode;
using CashCode;
using System.Collections.Generic;
using System.Collections.Generic;
using System.IO.Ports;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading.Tasks;
using System.Timers;
using WebSocketServer_1C;

public class CashValidatorPayment : PaymentBase
{
    private bool _isClosed;
    private bool _isErrorHandled;

    private bool _isDisposing = false;

    private int _sum = 0;
    private long _needSum = 0;
    private readonly CashCodeBillValidator _cashCodeBV;
    private readonly object _disposeLock = new();
    System.Timers.Timer pingTimer;

    public CashValidatorPayment(string portName, long targetSum, int baudRate = 9600,
        Dictionary<int, int>? cashCodeTable = null)
    {
        _needSum = targetSum;
        _cashCodeBV = new CashCodeBillValidator(portName, baudRate);
        _cashCodeBV.ConnectBillValidator();
        UpdateCashTable();
        try
        {
            _cashCodeBV.PowerUpBillValidator();
            var enableStatus = _cashCodeBV.EnableBillValidator();
            if (enableStatus != 0)
                throw new Exception($"EnableBillValidator returned {enableStatus}");
        }
        catch
        {
            _cashCodeBV.Dispose();
            throw;
        }

        _cashCodeBV.BillException += OnBillException; //---
        _cashCodeBV.BillStacking += OnBillStacking; //---
        _cashCodeBV.BillReceived += CashCodeBvOnBillReceived;
        _cashCodeBV.StartListening();
        StartPolling();
        GetCashTable(); //---
        FileLogger.Log("[CASH] Created");
    }

    private void OnBillException(object sender, BillExceptionEventArgs e) //---
    {
        FileLogger.Log("[CASH] BillException: " + JsonSerializer.Serialize(e));
        Task.Run(HandleFatalError);
    }

    private void OnBillStacking(object sender, BillStackedEventArgs e) //---
    {
        FileLogger.Log("[CASH] BillStacked: " + JsonSerializer.Serialize(e));
    }

    private void StartPolling()
    {
        pingTimer = new(2000);
        pingTimer.Elapsed += PingTimerHandler;
        pingTimer.Start();
    }

    private void StopPolling()
    {
        pingTimer.Stop();
        pingTimer.Elapsed -= PingTimerHandler;
        pingTimer.Dispose();
    }

    void PingTimerHandler(object sender, ElapsedEventArgs e)
    {
        bool isHealthy;
        lock (_disposeLock)
        {
            if (_isDisposing) return;
            try { isHealthy = _cashCodeBV.IsConnected; }
            catch { isHealthy = false; }
        }

        if (!isHealthy) HandleFatalError();
    }

    private void HandleFatalError()
    {
        lock (_disposeLock)
        {
            if (_isDisposing) return;
            _isDisposing = true;

            try { StopPolling(); } catch { }

            _cashCodeBV.BillReceived -= CashCodeBvOnBillReceived;
            _cashCodeBV.BillStacking -= OnBillStacking;
            _cashCodeBV.BillException -= OnBillException;
            try { _cashCodeBV.Dispose(); } catch { }

            FileLogger.Log("[CASH] Disabled by error");

            if (!_isErrorHandled)
            {
                _isErrorHandled = true;
                _isClosed = true;
                OnError();
            }
        }
    }

    private void CashCodeBvOnBillReceived(object sender, BillReceivedEventArgs e)
    {
        FileLogger.Log("[CASH] CashCode received bill");
        FileLogger.Log("[CASH] BillReceivedStatus: " + e.Status);
        if (e.Status != BillRecievedStatus.Accepted) return;
        _sum += e.Value;
        OnPartResult(e.Value);
    }

    private void UpdateCashTable()
    {
        var cashTable = JsonSerializer.Deserialize<Dictionary<int, int>>(File.ReadAllText("CashConfig.json"));
        FileLogger.Log("[CASH] CashTable: " + JsonSerializer.Serialize(cashTable)); //---
        var field = typeof(CashCodeBillValidator)
            .GetField("CashCodeTable", BindingFlags.NonPublic | BindingFlags.Instance);
        FileLogger.Log(field?.FieldType + " " + field?.Name); //---
        field?.SetValue(_cashCodeBV, cashTable);
    }

    private void GetCashTable() //---
    {
        var field = typeof(CashCodeBillValidator)
            .GetField("CashCodeTable", BindingFlags.NonPublic | BindingFlags.Instance);
        FileLogger.Log("[CASH] ������� � ��������� ���� " + JsonSerializer.Serialize(field?.GetValue(_cashCodeBV)));
    }

public override void CloseSession()
    {
        lock (_disposeLock)
        {
            if (_isClosed || _isDisposing) return;

            _isDisposing = true;
            StopPolling();

            try
            {
                _cashCodeBV.DisableBillValidator();
                _cashCodeBV.StopListening();
                _cashCodeBV.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CASH] Error during close: {ex.Message}");
            }

            _isClosed = true;
            FileLogger.Log("[CASH] Closed");
        }
    }
}