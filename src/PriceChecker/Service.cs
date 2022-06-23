using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MetricsAccumulator;
using MetricsAccumulator.Graphite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using NLog;
using Pepper;
using Pepper.Consuming;
using Price.Infrastructure.Consumer;
using Price.Infrastructure.DeadLetter;
using Price.Infrastructure.Gateway;
using Price.Infrastructure.Message.Inbound;
using Price.Infrastructure.OptimisticConcurrency;
using Price.Infrastructure.Pepper;
using Price.Infrastructure.RabbitMq;
using RabbitMQ.Client;

namespace PriceChecker;

public class Service : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetLogger(typeof(Service).FullName);
    private readonly IConfiguration _config;
    private ServiceDependencies? _serviceDependencies;

    public Service(IConfiguration config) => _config = config;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Starting service...");
        _serviceDependencies = SetupCompositionRoot(_config);
        _serviceDependencies.RabbitMqDependencies.Consumer.CreateQueuesAndExchanges();
        _serviceDependencies.RabbitMqDependencies.Consumer.Start();
        _serviceDependencies.PepperBus.Start();
        Logger.Info("Started service");
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        Logger.Info("Stopping service...");
        if (_serviceDependencies != null)
        {
            _serviceDependencies.MetricsScheduler.Dispose();
            _serviceDependencies.GraphiteReporter.Dispose();
            _serviceDependencies.RabbitMqDependencies.Consumer.Dispose();
            _serviceDependencies.RabbitMqDependencies.Publisher.Dispose();
            _serviceDependencies.RabbitMqDependencies.PublishConnection.Dispose();
            _serviceDependencies.RabbitMqDependencies.ConsumeConnection.Dispose();
            _serviceDependencies.PepperBus.Stop();
        }
        Logger.Info("Stopped service");
    }

    private static ServiceDependencies SetupCompositionRoot(IConfiguration config)
    {
        //TO DO - RabbitMQ Connection verify
        var metricsCollector = new MetricsCollector();
        var metricsReporter = CreateMetricsReporter(config);
        var metricsScheduler = SetUpMetricsScheduler(metricsReporter.Value, metricsCollector);
        var deadLetterDb = new MongoDb(config["ConnectionStrings:DeadLetters"]);
        var deadLetterGateway = new DeadLetterGateway(deadLetterDb.GetDatabase());
        var rabbitMqDependencies = CreateRabbitMqConsumer(config, metricsCollector, deadLetterGateway);
        var pepperBus = CreatePepperBus(config, metricsCollector, deadLetterDb);
        return new ServiceDependencies(metricsScheduler, metricsReporter, rabbitMqDependencies, pepperBus);
    }

    private static RabbitMqDependencies CreateRabbitMqConsumer(
        IConfiguration config,
        MetricsCollector metricsCollector,
        DeadLetterGateway deadLetterGateway)
    {
        var rabbitMqPublishConnection = CreateRabbitMqConnection(config);
        var rabbitMqPublisher = new RabbitMqConfirmedPublisher(rabbitMqPublishConnection);
        var simplePublisher = new SimplePublisherMetrics(
                                new DeclaringSimplePublisher(
                                    new SimplePublisher(rabbitMqPublisher), rabbitMqPublishConnection),
                                    metricsCollector);

        var rabbitMqConsumeConnection = CreateRabbitMqConnection(config);
        var rabbitMqConsumerBuilder = new RabbitMqConsumerBuilder(rabbitMqConsumeConnection, rabbitMqPublisher, config["RabbitMQ:QueueName"])
            .WithPrefetchCount(ushort.Parse(config["RabbitMQ:PrefetchCount"]))
            .WithRetryIntervals(new[] { 1000, 3000 })
            .WithRetryableExceptionPredicate(ex =>
                ex is OptimisticConcurrencyException
                || ex is TimeoutException
                || ex is MongoConnectionException && ex.InnerException is EndOfStreamException)
            .WithErrorHandler((eventArgs, exception) => deadLetterGateway.HandleError(eventArgs, exception, config["RabbitMQ:DeadLetterCollectionName"], RabbitMqConsumer.GetFallbackQueueName(config["RabbitMQ:QueueName"])));

        WithProcessor<FaultyMessage>(new FaultyProcessor());
        WithProcessor<Ping>(new PingProcessor(simplePublisher));

        var rabbitMqConsumer = rabbitMqConsumerBuilder.Build();
        return new RabbitMqDependencies(rabbitMqPublishConnection, rabbitMqConsumeConnection, rabbitMqPublisher, rabbitMqConsumer);

        void WithProcessor<T>(IProcessor processor, string exchangeType = ExchangeType.Fanout, string routingKey = "")
        {
            var type = MessagingUtil.GetMessageTypeUrn(typeof(T).Name);
            var decoratedProcessor = new ConsumerMetrics<T>(new OptimisticConcurrencyRetryer(processor), metricsCollector);
            rabbitMqConsumerBuilder.WithProcessor(type, decoratedProcessor, exchangeType, routingKey);
        }
    }

    private static IConnection CreateRabbitMqConnection(IConfiguration configuration) =>
        new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = false,
            VirtualHost = configuration["RabbitMQ:VirtualHost"],
            UserName = configuration["RabbitMQ:Username"],
            Password = configuration["RabbitMQ:Password"],
            Port = int.Parse(configuration["RabbitMQ:Port"])
        }.CreateConnection(configuration["RabbitMQ:Hosts"].Split("|"), $"{configuration["RabbitMQ:ApplicationName"]}-{Environment.MachineName}");

    private static PepperBus CreatePepperBus(
        IConfiguration config,
        MetricsCollector metricsCollector,
        MongoDb deadLetterDb)
    {
        var pepperBus = new PepperBusBuilder()
             .WithQueueManager(config["Pepper:QueueManagerName"])
             .WithHosts(config["Pepper:Host"])
             .WithUserId(config["Pepper:UserId"])
             .WithUserPassword(config["Pepper:UserPassword"])
             .WithChannelName(config["Pepper:ChannelName"])
             .Build();
        WithOptionalProcessor(config["Pepper:FaultyMessageQueueName"], nameof(PepperFaultyProcessor), new PepperFaultyProcessor());
        return pepperBus;

        void WithOptionalProcessor(string queueName, string baseMetricName, IPepperProcessor processor)
        {
            if (string.IsNullOrEmpty(queueName)) return;
            var decoratedProcessor = new PepperConsumerMetrics(processor, metricsCollector, baseMetricName);
            var pepperConsumer = PepperConsumer
                .ConsumeText(async message => await decoratedProcessor.Process(message.Content))
                .UseFallbackStrategy((exception, s) => PepperDeadLetterGateway.HandleError(s, exception, queueName, config["ServiceName"], deadLetterDb.GetDatabase()))
                .Create();
            pepperBus.ForQueue(queueName).SubscribeConsumer(pepperConsumer);
        }
    }

    private static Own<IReporter> CreateMetricsReporter(IConfiguration config) =>
        #if true
        MetricsAccumulatorGraphite.ReporterFrom(
            config["MetricsAccumulator:Graphite:Hostname"],
            int.Parse(config["MetricsAccumulator:Graphite:Port"]),
            config["MetricsAccumulator:Graphite:Prefix"]);
        #else
        // Turn this on to output metrics as logs
        new Own<IReporter>(new LoggingMetricsReporter());
        #endif

    private static IDisposable SetUpMetricsScheduler(IReporter metricsReporter, MetricsCollector metricsCollector)
    {
        var schedulerConfiguration = Scheduler.Configure(configuration =>
            configuration.WithPeriod(TimeSpan.FromSeconds(1))
                .WithCollector(metricsCollector)
                .WithReporter(metricsReporter)
                .WithExceptionHandler(ex => Logger.Warn(ex, "Metrics error")));

        var metricsScheduler = schedulerConfiguration.Start();
        return metricsScheduler;
    }

    private record RabbitMqDependencies(
        IConnection PublishConnection,
        IConnection ConsumeConnection,
        IRabbitMqPublisher Publisher,
        RabbitMqConsumer Consumer);

    private record ServiceDependencies(
        IDisposable MetricsScheduler,
        Own<IReporter> GraphiteReporter,
        RabbitMqDependencies RabbitMqDependencies,
        PepperBus PepperBus);
}