using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CarRental.Core;
using CarRental.Core.Features;

namespace CarRental.Tests
{
    public class RentalServiceTests : IDisposable
    {
        private readonly RentalDbContext _context;
        private readonly string _databaseName;
        private readonly CreateReservationHandler _createHandler;
        private readonly GetAvailableCarTypesHandler _queryHandler;

        public RentalServiceTests()
        {
            // Load appsettings.json dynamically for integration testing configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            _databaseName = $"CarRentalTest_{Guid.NewGuid():N}";
            string connString = string.Format(config.GetConnectionString("TestConnectionPattern")!, _databaseName);

            // Establish an isolated physical SQL Server database instance for this test run
            var options = new DbContextOptionsBuilder<RentalDbContext>()
                .UseSqlServer(connString)
                .Options;

            _context = new RentalDbContext(options);
            _context.Database.EnsureCreated();

            // Manually instantiate handlers bypassing the MediatR pipeline overhead in testing
            _createHandler = new CreateReservationHandler(_context, NullLogger<CreateReservationHandler>.Instance);
            _queryHandler = new GetAvailableCarTypesHandler(_context);
        }

        public void Dispose()
        {
            // Teardown: Safely drop the isolated database from the local SQL instance after each test
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private async Task SeedBaseDataAsync()
        {
            _context.CarTypes.AddRange(
                new CarTypeEntity { TypeName = "SUV", Description = "Sport utility vehicles" },
                new CarTypeEntity { TypeName = "Sedan", Description = "Standard passenger cars" },
                new CarTypeEntity { TypeName = "Van", Description = "Family vans" }
            );
            _context.Cars.AddRange(
                new Car { CarId = "SUV_01", CarTypeName = "SUV" },
                new Car { CarId = "SEDAN_01", CarTypeName = "Sedan" },
                new Car { CarId = "SEDAN_02", CarTypeName = "Sedan" }
            );
            await _context.SaveChangesAsync();
        }

        // =========================================================================
        // CORE BUSINESS REQUIREMENTS TESTS
        // =========================================================================

        [Fact]
        public async Task System_ShouldAllow_ReservationOfCarOfGivenType_AtDesiredDate_ForGivenDays()
        {
            // Requirement Check: The system should allow reservation of a car of a given type at a desired date for a given number of days.
            // Arrange
            await SeedBaseDataAsync();
            var desiredDate = new DateTime(2026, 11, 20, 10, 0, 0);
            int desiredDays = 5;
            var command = new CreateReservationCommand("Van", desiredDate, desiredDays, Guid.NewGuid());

            // Act
            var result = await _createHandler.Handle(command, default);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Van", result.CarType);
            Assert.Equal(desiredDate, result.StartDate);
            Assert.Equal(desiredDate.AddDays(desiredDays), result.EndDate);
        }

        [Fact]
        public async Task System_ShouldReturnNull_WhenFleetLimitIsExceeded()
        {
            // Requirement Check: The number of cars of each type is limited.
            // Arrange
            await SeedBaseDataAsync(); // Pool has exactly 1 SUV ("SUV_01")
            var startDate = new DateTime(2026, 11, 20, 10, 0, 0);

            var firstClient = new CreateReservationCommand("SUV", startDate, 3, Guid.NewGuid());
            var secondClient = new CreateReservationCommand("SUV", startDate, 3, Guid.NewGuid());

            // Act
            var firstResult = await _createHandler.Handle(firstClient, default);
            var secondResult = await _createHandler.Handle(secondClient, default); // No cars left in this timeframe

            // Assert
            Assert.NotNull(firstResult); // First client gets the only SUV
            Assert.Null(secondResult);   // Second client receives null due to fleet limits
        }

        // =========================================================================
        // ADVANCED ARCHITECTURE & RESILIENCE TESTS
        // =========================================================================

        [Fact]
        public async Task MakeReservation_ShouldPublishOutboxMessage_WhenCarIsAvailable()
        {
            // Outbox Pattern Check: Ensure notifications are transactional
            // Arrange
            await SeedBaseDataAsync();
            var command = new CreateReservationCommand("SUV", DateTime.UtcNow.AddDays(1), 3, Guid.NewGuid());

            // Act
            var result = await _createHandler.Handle(command, default);

            // Assert
            Assert.NotNull(result);

            // Verify that the notification was committed atomically inside the same database transaction
            var outboxMessage = await _context.OutboxMessages.FirstOrDefaultAsync();
            Assert.NotNull(outboxMessage);
            Assert.Equal("ReservationConfirmed", outboxMessage.Type);
            Assert.Null(outboxMessage.ProcessedOnUtc); // Worker service has not fetched it yet
        }

        [Fact]
        public async Task MakeReservation_ShouldEnforceIdempotency_WhenDuplicateKeyIsSubmitted()
        {
            // Idempotency Check: Protect against accidental double-clicks or partner B2B network retries
            // Arrange
            await SeedBaseDataAsync();
            var sharedKey = Guid.NewGuid();
            var command1 = new CreateReservationCommand("Sedan", DateTime.UtcNow.AddDays(2), 2, sharedKey);
            var command2 = new CreateReservationCommand("Sedan", DateTime.UtcNow.AddDays(2), 2, sharedKey);

            // Act
            var result1 = await _createHandler.Handle(command1, default);
            var result2 = await _createHandler.Handle(command2, default);

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal(result1.ReservationId, result2.ReservationId); // Handled gracefully via idempotent lookup

            // Validate database state: Exactly 1 record committed
            var totalReservations = await _context.Reservations.CountAsync(r => r.IdempotencyKey == sharedKey);
            Assert.Equal(1, totalReservations);
        }

        [Fact]
        public async Task ParallelReservations_ShouldEnforceOptimisticConcurrency_UnderRaceConditions()
        {
            // High-Concurrency/DDoS Check: Two nodes/threads attempting to claim the last car at the exact same millisecond
            // Arrange
            await SeedBaseDataAsync(); // Pool has exactly 1 SUV

            var cmd1 = new CreateReservationCommand("SUV", DateTime.UtcNow.AddDays(5), 3, Guid.NewGuid());
            var cmd2 = new CreateReservationCommand("SUV", DateTime.UtcNow.AddDays(5), 3, Guid.NewGuid());

            // Act - Dispatch parallel tasks onto separate threads simultaneously
            var task1 = Task.Run(() => _createHandler.Handle(cmd1, default));
            var task2 = Task.Run(() => _createHandler.Handle(cmd2, default));

            await Task.WhenAll(task1, task2);

            int successCount = 0;

            var result1 = await task1;
            if (result1 != null) successCount++;

            var result2 = await task2;
            if (result2 != null) successCount++;

            // Assert - RowVersion concurrency fencing check
            // Exactly one booking must succeed, the other must be blocked by DbUpdateConcurrencyException and return null
            Assert.Equal(1, successCount);

            var dbCount = await _context.Reservations.CountAsync(r => r.CarId == "SUV_01");
            Assert.Equal(1, dbCount);
        }

        [Fact]
        public async Task GetAvailableCarTypes_ShouldReturnCorrectQuotas_UsingCQRSReadPipeline()
        {
            // CQRS Pipeline Check
            // Arrange
            await SeedBaseDataAsync();
            var start = DateTime.UtcNow.AddDays(1);

            // Occupy one sedan in advance
            await _createHandler.Handle(new CreateReservationCommand("Sedan", start, 2, Guid.NewGuid()), default);

            // Act - Dispatch a query via the CQRS read pipeline
            var query = new GetAvailableCarTypesQuery(start, 2);
            var result = await _queryHandler.Handle(query, default);

            // Assert
            var sedanQuota = result.First(r => r.TypeName == "Sedan");
            var suvQuota = result.First(r => r.TypeName == "SUV");

            Assert.Equal(1, sedanQuota.AvailableCount); // 2 total - 1 reserved = 1 remaining
            Assert.Equal(1, suvQuota.AvailableCount);   // 1 total - 0 reserved = 1 remaining
        }
    }
}

