using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using NLog.Extensions.Hosting;
using Price.Logging.NLog;

namespace PriceChecker;

public class Program
{
    private static readonly Logger Logger = LogManager.GetLogger(typeof(Program).FullName);

    public static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.override.json", optional: true, reloadOnChange: false)
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("AppSettings.Override") ?? "{}")))
            .AddEnvironmentVariables()
            .Build();

        ConfigureNLog(config);

        Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddHostedService(_ => new Service(config)))
            .UseWindowsService(options => options.ServiceName = config["ServiceName"])
            .UseNLog()
            .Build()
            .Run();
    }

    private static void ConfigureNLog(IConfiguration configuration) =>
        NLogConfigurator.Configure(
            new ConfigurationExtractorBuilder()
                .WithDotNetCoreConfiguration(configuration)
                .Build()
                .Extract());

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        NLog.Fluent.Log.Fatal()
            .Message($"{e.ExceptionObject.GetType()} UnhandledException - {((Exception)e.ExceptionObject).Message}")
            .LoggerName(Logger.Name)
            .Exception((Exception)e.ExceptionObject)
            .Write();

    private static void TaskSchedulerOnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        NLog.Fluent.Log.Fatal()
            .Message($"{e.Exception.GetBaseException().GetType()} UnobservedTaskException - {e.Exception.GetBaseException().Message}")
            .LoggerName(Logger.Name)
            .Exception(e.Exception.GetBaseException())
            .Write();
}