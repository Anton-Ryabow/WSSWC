namespace WebSocketServer_1C;

public class FileLogger
{
    private static readonly string LogsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private static readonly object LockObject = new object();

    static FileLogger()
    {
        // Создаем папку Logs если ее нет
        if (!Directory.Exists(LogsDirectory))
        {
            Directory.CreateDirectory(LogsDirectory);
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (LockObject)
            {
                string fileName = $"log_{DateTime.Now:yyyyMMdd}.txt";
                string filePath = Path.Combine(LogsDirectory, fileName);
                
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                
                File.AppendAllText(filePath, logEntry);
                
                Console.WriteLine(message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOGGER ERROR] Failed to write to log file: {ex.Message}");
            Console.WriteLine(message);
        }
    }
}