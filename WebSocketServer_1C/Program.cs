using CashCode;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using WebSocketServer_1C;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

IntPtr window = GetConsoleWindow();
ShowWindow(window, int.Parse(builder.Configuration["ShowConsole"]));

bool createdNew;
Mutex mutex = new Mutex(true, "MyWebSocketMutex", out createdNew);

if (!createdNew)
{
    ShowWindow(window, 1);
    Console.WriteLine(">>> Сервер уже запущен!");
    await Task.Delay(1000);
    return;
}

app.Urls.Add(builder.Configuration["Host"]);

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

Session? session = null;

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Endpoint only accepts WebSocket requests.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    FileLogger.Log("[SERVER] Client connected: " + context.Connection.RemoteIpAddress);
    
    var buffer = new byte[4 * 1024];
    var ct = context.RequestAborted;
    
    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", ct);
                    FileLogger.Log("[SERVER] Client closed connection.");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(ms.ToArray());
                FileLogger.Log("[SERVER] Received: " + message);
                try
                {
                    var jsonDoc = JsonDocument.Parse(message);
                    var root = jsonDoc.RootElement;
                    if (root.TryGetProperty("Command", out var cmdElement))
                    {
                        string command = cmdElement.GetString() ?? "";
                        if (command == "ValidatorStart")
                        {
                            if (session is not null)
                                session.CloseSession();
                            session = new Session(root, webSocket, ct, builder.Configuration["COM:cash"], builder.Configuration["COM:coin"]);
                        }
                        else if (command == "ValidatorStop")
                        {
                            FileLogger.Log("[SERVER] ValidatorStop received");
                            session?.CloseSession();
                        }
                    }
                }
                catch (TimeoutException ex)
                {
                    FileLogger.Log("[SERVER] Validator is unavailable: " + ex.Message);
                }
                catch (JsonException ex)
                {
                    FileLogger.Log("[SERVER] JSON parse or processing error: " + ex.Message);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        
    }
    catch (WebSocketException ex)
    {
        FileLogger.Log("[SERVER] WebSocket error: " + ex.Message);
        session?.CloseSession();
    }
});
app.Run();

class Session
{
    private bool _isClosed;

    private Guid _guid;
    private readonly int _targetSum;
    private CancellationToken ct;
    private int _currentSum = 0;
    private JsonElement _root;
    private WebSocket _webSocket;
    private CashValidatorPayment _validator;
    private CoinValidatorPayment _coinValidator;
    public Session(JsonElement root, WebSocket webSocket, CancellationToken token, string cashPort, string coinPort)
    {
        _root = root;
        _webSocket = webSocket;
        _targetSum = root.GetProperty("Sum").GetInt32();
        _guid = root.GetProperty("Guid").GetGuid();
        ct = token;

        ErrorEvent += async (error) =>
        {
            if (error == SessionError.Cash)
            {
                await SendStatusMessageAsync(new { Status = "CashCodeDisconnected", Guid = _guid });
                FileLogger.Log("[SESSION] CashCodeDisconnected");
                if (_validator != null) _validator.IsError = true;
                _validator?.CloseSession();
                if (_coinValidator != null && _coinValidator.IsError)
                {
                    await SendStatusMessageAsync(new { Status = "DevicesDisconnected", Guid = _guid });
                    FileLogger.Log("[SESSION] DevicesDisconnected");
                    CloseSession();
                }
            }
            else if (error == SessionError.Coin)
            {
                await SendStatusMessageAsync(new { Status = "MicroCoinDisconnected", Guid = _guid });
                FileLogger.Log("[SESSION] MicroCoinDisconnected");
                if (_coinValidator != null) _coinValidator.IsError = true;
                _coinValidator?.CloseSession();
                if (_validator == null || _validator.IsError)
                {
                    await SendStatusMessageAsync(new { Status = "DevicesDisconnected", Guid = _guid });
                    FileLogger.Log("[SESSION] DevicesDisconnected");
                    CloseSession();
                }
            }
        };

        DeviceInitializedEvent += async (messageObject) =>
        {
            await SendStatusMessageAsync(messageObject);
        };

        try
        {
            _validator = new CashValidatorPayment(cashPort, _targetSum);
            OnDeviceInitialized(new { Status = "CashCode Connected"});
            FileLogger.Log("[SESSION] CashCode Connected");
        }
        catch(Exception ex)
        {
            OnError(SessionError.Cash);
        }
        try
        {
            _coinValidator = new CoinValidatorPayment(coinPort);
            OnDeviceInitialized(new { Status = "MicroCoin Connected" });
            FileLogger.Log("[SESSION] MicroCoin Connected");
        }
        catch (Exception ex)
        {
            OnError(SessionError.Coin);
        }
        
        if (_validator != null)
        {
            _validator.PartPaymentEvent += async (int amount) =>
            {
                _currentSum += amount;
                await SendStatusMessageAsync(new
                    { DateTime = DateTime.Now, Guid = _guid, Accepted = amount, Device = "CashCode" });
                FileLogger.Log($"[SESSION] Accepted {amount} for {_guid}");
                if (_currentSum < _targetSum) return;
                await SendStatusMessageAsync(new { Status = "FullSumAccepted", Guid = _guid });
                FileLogger.Log($"[SESSION] Full sum accepted for {_guid}");
                CloseSession();
            };
            _validator.ErrorEvent += async () =>
            {
                await SendStatusMessageAsync(new { Status = "CashCodeDisconnected", Guid = _guid });
                FileLogger.Log("[SESSION] CashCodeDisconnected");
                _validator.IsError = true;
                _validator.CloseSession();
                if (_coinValidator == null || _coinValidator.IsError)
                {
                    await SendStatusMessageAsync(new { Status = "DevicesDisconnected", Guid = _guid });
                    FileLogger.Log("[SESSION] DevicesDisconnected");
                    CloseSession();
                }
            };
        }

        if (_coinValidator != null)
        {
            _coinValidator.ErrorEvent += async () =>
            {
                await SendStatusMessageAsync(new { Status = "MicroCoinDisconnected", Guid = _guid });
                FileLogger.Log("[SESSION] MicroCoinDisconnected");
                _coinValidator.IsError = true;
                _coinValidator.CloseSession();
                if (_validator == null || _validator.IsError)
                {
                    await SendStatusMessageAsync(new { Status = "DevicesDisconnected", Guid = _guid });
                    FileLogger.Log("[SESSION] DevicesDisconnected");
                    CloseSession();
                }
            };
            _coinValidator.PartPaymentEvent += async (int amount) =>
            {
                _currentSum += amount;
                await SendStatusMessageAsync(new
                    { DateTime = DateTime.Now, Guid = _guid, Accepted = amount, Device = "MicroCoin" });
                FileLogger.Log($"[SESSION] Accepted {amount} for {_guid}");
                if (_currentSum < _targetSum) return;
                await SendStatusMessageAsync(new { Status = "FullSumAccepted", Guid = _guid });
                FileLogger.Log($"[SESSION] Full sum accepted for {_guid} (from micro)");
                CloseSession();
            };
        }
        FileLogger.Log("[SESSION] Created");
    }

    public void CloseSession()
    {
        if (_isClosed) return;
        _validator?.CloseSession();
        _coinValidator?.CloseSession();
        _isClosed = true;
        FileLogger.Log("[SESSION] Closed");
    }

    private async Task SendStatusMessageAsync(object messageObject)
    {
        var message = messageObject;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void OnError(SessionError sessionError)
    {
        ErrorEvent?.Invoke(sessionError);
    }

    private void OnDeviceInitialized(object messageObject)
    {
        DeviceInitializedEvent?.Invoke(messageObject);
    }

    public delegate void DeviceInitializedHandler(object messageObject);
    public delegate void ErrorHandler(SessionError error);
    public event DeviceInitializedHandler DeviceInitializedEvent;
    public event ErrorHandler ErrorEvent;
}

internal enum SessionError
{
    Cash,
    Coin
}