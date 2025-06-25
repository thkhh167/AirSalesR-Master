using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Timers;
using Sales;
using SuperSimpleTcp;
using Timer = System.Timers.Timer;

const byte departure = 0x5D;
const byte weAreTooRichNow = 0x7A;
const byte allFlightsSoldOut = 0x9E;

const int nFlights = 3;
const int nWorkers = 5;

Timer? clientCreationTimer = null;
var cts = new CancellationTokenSource();
var clientQueue = new ConcurrentQueue<Client>();
bool hasEnded = false;
object endLock = new object();
object consolelock = new object();
SalesStat salesStat = new();

var flights = new List<Flight>();
for (int i = 0; i < nFlights; i++)
    flights.Add(new Flight());

string guid = "AirSalesProjectMMF";
MemoryMappedFile mmf = MemoryMappedFile.CreateOrOpen(guid, 4096);

string mutexName = $"Global\\mutex-{guid}";
var mutex = new Mutex(false, mutexName);

SimpleTcpServer? server = null;
string? managerConnection = null;

StartTcpServer();

Console.WriteLine("Press any key when manager process ready..");
Console.ReadKey();

StartClientFactory();
StartWorkerThreads();

Console.ReadLine();
return;

void StartTcpServer()
{
    server = new SimpleTcpServer("127.0.0.1:17000");
    server.Events.ClientConnected += TcpServerOnClientConnected;
    server.Events.DataReceived += TcpServerOnDataReceived;
    server.Start();

    PrintWithLock("[StartTcpServer] [Sales] TCP Server started on 127.0.0.1:17000");
}

void TcpServerOnClientConnected(object? sender, ConnectionEventArgs e)
{
    managerConnection = e.IpPort;
    PrintWithLock($"[TcpServerOnClientConnected] [Sales] Manager connected: {e.IpPort}");
}

void TcpServerOnDataReceived(object? sender, DataReceivedEventArgs e)
{
    if (e.Data.Count != 1) return;
    byte code = e.Data[0];

    switch (code)
    {
        case weAreTooRichNow:
            PrintWithLock("[TcpServerOnDataReceived] [Sales] Received code: WeAreTooRichNow");
            EndOfSales(Reason.TooRich);
            break;

        case departure:
            PrintWithLock("[TcpServerOnDataReceived] [Sales] Received code: Departure");
            EndOfSales(Reason.Departure);
            break;

        default:
            PrintWithLock($"[TcpServerOnDataReceived] [Sales] Received unknown code: {code}");
            break;
    }
}

void UpdateSharedMemory()
{
    try
    {
        mutex.WaitOne();

        using var accessor = mmf.CreateViewAccessor();
        byte[] revenueBytes = BitConverter.GetBytes(salesStat.TotalRevenue);
        byte[] clientsBytes = BitConverter.GetBytes(salesStat.TotalClientsServed);

        accessor.WriteArray(0, revenueBytes, 0, revenueBytes.Length);
        accessor.WriteArray(4, clientsBytes, 0, clientsBytes.Length);
    }
    finally
    {
        mutex.ReleaseMutex();
    }
}

void Sell(object? obj)
{
    if (obj == null) return;
    var worker = (Worker)obj;

    int localRevenue = 0;
    int localClientsServed = 0;

    while (!cts.Token.IsCancellationRequested && !hasEnded)
    {
        if (!clientQueue.TryDequeue(out Client? client))
        {
            Thread.Sleep(10);
            continue;
        }

        bool sold = false;
        int cost = 0;

        foreach (var flight in flights)
        {
            if (flight.TryBookSeat(out cost))
            {
                sold = true;
                break;
            }
        }

        if (!sold)
        {
            EndOfSales(Reason.SoldOut);
            break;
        }

        localRevenue += cost;
        localClientsServed++;

        PrintWithLock($"[Sell] [{worker.Id}]Worker sold {(cost == Flight.FirstClassCost ? "First class" : "Economy class")} to [{client.Id}]Client");

        salesStat.TotalRevenue += cost;
        salesStat.TotalClientsServed++;

        UpdateSharedMemory();
    }
}

void EndOfSales(Reason reason)
{
    lock (endLock)
    {
        if (hasEnded) return;
        hasEnded = true;
    }

    cts.Cancel();

    clientCreationTimer?.Stop();

    PrintWithLock($"END OF SALES: {reason switch
    {
        Reason.Departure => "DEPARTURE",
        Reason.TooRich => "WE ARE TOO RICH NOW",
        Reason.SoldOut => "OUT SOLD FLIGHTS ALL",
        _ => "UNKNOWN REASON"
    }}");

    if (reason == Reason.SoldOut && managerConnection != null && server != null)
    {
        server.Send(managerConnection, new byte[] { allFlightsSoldOut });
    }
}

void StartWorkerThreads()
{
    for (int i = 0; i < nWorkers; i++)
    {
        var worker = new Worker(cts.Token);
        var thread = new Thread(Sell) { IsBackground = true };
        thread.Start(worker);
    }
}

void CreateClient(object? sender, ElapsedEventArgs e)
{
    var client = new Client();
    PrintWithLock($"[CreateClient] [{client.Id}]Client created");
    clientQueue.Enqueue(client);
}

void StartClientFactory()
{
    clientCreationTimer = new Timer();
    clientCreationTimer.Elapsed += CreateClient;
    clientCreationTimer.Interval = Random.Shared.Next(5, 26);
    clientCreationTimer.AutoReset = true;
    clientCreationTimer.Start();

    PrintWithLock("[StartClientFactory] [Sales] Client factory timer started.");
}

void PrintWithLock(string message)
{
    lock (consolelock)
    {
        Console.WriteLine(message);
    }
}