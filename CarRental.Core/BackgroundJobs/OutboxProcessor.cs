using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarRental.Core.BackgroundJobs
{
    public class DistributedOutboxProcessor : BackgroundService
    {
        // Isolation of the raw SQL query string from the business logic - move to future SQueryRepository
        private const string FetchOutboxMessagesSql = @"
        SELECT TOP (20) * 
        FROM OutboxMessages WITH (UPDLOCK, READPAST) 
        WHERE ProcessedOnUtc IS NULL 
        ORDER BY OccurredOnUtc";

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DistributedOutboxProcessor> _logger;

        public DistributedOutboxProcessor(IServiceProvider serviceProvider, ILogger<DistributedOutboxProcessor> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Distributed Outbox Background Processor started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<RentalDbContext>();

                    // Explicit transaction with ReadCommitted isolation level to ensure hints like UPDLOCK/READPAST work reliably
                    using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, stoppingToken);

                    // For Multi-node setup
                    var messages = await context.OutboxMessages
                        .FromSql($"{FetchOutboxMessagesSql}")
                        .ToListAsync(stoppingToken);

                    if (messages.Any())
                    {
                        foreach (var message in messages)
                        {
                            try
                            {
                                await Task.Delay(100, stoppingToken); // SMTP gateway simulation
                                message.ProcessedOnUtc = DateTime.UtcNow;
                                _logger.LogInformation("[Outbox Success] Dispatched by node: {Machine}", Environment.MachineName);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Message process fault for ID {MessageId}: {Message}", message.Id, ex.Message);
                                message.Error = ex.ToString();
                            }
                        }

                        await context.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken); // Row locks released here

                        // Performance tweak: If there were messages, immediately loop again to drain the queue fast
                        continue;
                    }
                    else
                    {
                        // Rollback empty transaction immediately to free up connection pool resources
                        await transaction.RollbackAsync(stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical Outbox multi-node cycle fault: {Message}", ex.Message);
                }

                // This delay now only triggers if NO messages were found, preventing empty database polling overload
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

    }
}
