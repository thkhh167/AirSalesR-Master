using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Timers;
using Manager;
using SuperSimpleTcp;
using Timer = System.Timers.Timer;

const byte departure = 0x5D;
const byte weAreTooRichNow = 0x7A;
const byte allFlightsSoldOut = 0x9E;

SimpleTcpClient? tcpClient = null;
Timer? mmfTimer = null;
Timer? departureTimer = null;
MemoryMappedFile? mmf = null;
Mutex? mutex = null;
SalesStat salesStats = new();
bool operationEnded = false;
object lockObj = new();

const int weAreTooRichThreshold = 600000; //Money Threshold

string guid = "AirSalesProjectMMF";
string mutexName = $"Global\\mutex-{guid}";
System.Threading.Thread.Sleep(3000);

try
{
    mmf = MemoryMappedFile.OpenExisting(guid);
    mutex = new Mutex(false,mutexName);
}
catch
{
    Console.WriteLine("[Manager] MMF or Mutex not found, ensure Sales is running first.");
    return;
}

tcpClient = new SimpleTcpClient("127.0.0.1:17000");
tcpClient.Events.DataReceived += OnDataReceived;

try
{
    tcpClient.Connect();
    Console.WriteLine("[Manager] Connected to Sales TCP Server on 127.0.0.1:17000");
}
catch (Exception ex)
{
    Console.WriteLine($"[Manager] Failed to connect TCP: {ex.Message}");
    return;
}
//Shared memory reader timer
mmfTimer = new Timer(1000); 
mmfTimer.Elapsed += ReadMmfEverySecond;
mmfTimer.AutoReset = true;
mmfTimer.Start();

departureTimer = new Timer(150000); //Time interval in milliseconds
departureTimer.Elapsed += DepartureTimerOnElapsed;
departureTimer.AutoReset = false;
departureTimer.Start();

Console.ReadLine();

void OnDataReceived(object? sender, DataReceivedEventArgs e)
{
    if (e.Data.Count != 1) return;

    byte code = e.Data[0];

    if (code == allFlightsSoldOut)
    {
        lock (lockObj)
        {
            if (operationEnded) return;
            operationEnded = true;
        }

        Console.WriteLine("[OnDataReceived] [Manager] Received code from Sales: AllFlightsSoldOut");
        StopTimers();
        Console.WriteLine("END OF PROGRAM: ALL FLIGHTS SOLD OUT");
    }
}

void StopTimers()
{
    mmfTimer?.Stop();
    departureTimer?.Stop();
}

void EndOfOperation(Reason reason)
{
    lock (lockObj)
    {
        if (operationEnded) return;
        operationEnded = true;
    }

    StopTimers();

    Console.WriteLine(reason switch
    {
        Reason.Departure => "END OF PROGRAM: DEPARTURE",
        Reason.TooRich => "END OF PROGRAM: WE ARE TOO RICH NOW",
        Reason.SoldOut => "END OF PROGRAM: ALL FLIGHTS SOLD OUT",
        _ => "END OF PROGRAM: UNKNOWN"
    });
}

void DepartureTimerOnElapsed(object? sender, ElapsedEventArgs e)
{
    lock (lockObj)
    {
        if (operationEnded) return;
    }

    tcpClient?.Send(new byte[] { departure });
    Console.WriteLine("[DepartureTimerOnElapsed] [Manager] Departure timer elapsed: sending Departure code to Sales");

    EndOfOperation(Reason.Departure);
}

void ReadMmfEverySecond(object? sender, ElapsedEventArgs e)
{
    if (mmf == null || mutex == null) return;

    try
    {
        mutex.WaitOne();

        using var accessor = mmf.CreateViewAccessor();
        byte[] buffer = new byte[8]; // 2x Int32: Revenue, ClientsServed
        accessor.ReadArray(0, buffer, 0, buffer.Length);

        int revenue = BitConverter.ToInt32(buffer, 0);
        int clients = BitConverter.ToInt32(buffer, 4);

        salesStats.TotalRevenue = revenue;
        salesStats.TotalClientsServed = clients;

        Console.WriteLine($"[ReadMmfEverySecond] [Manager] Reading mmf data : {salesStats}");

        if (salesStats.TotalRevenue >= weAreTooRichThreshold)
        {
            Console.WriteLine("[ReadMmfEverySecond] [Manager] We are so rich now... Sending WeAreTooRichNow code to Sales");
            tcpClient?.Send(new byte[] { weAreTooRichNow });
            EndOfOperation(Reason.TooRich);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ReadMmfEverySecond] Error reading MMF or acquiring mutex: {ex.Message}");
    }
    finally
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception releaseEx)
        {
            Console.WriteLine($"[ReadMmfEverySecond] Failed to release mutex: {releaseEx.Message}");
        }
    }
}
