using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Core
{
    public class RentalDbContext : DbContext
    {
        public RentalDbContext(DbContextOptions<RentalDbContext> options) : base(options) { }

        public DbSet<Car> Cars => Set<Car>();
        public DbSet<CarTypeEntity> CarTypes => Set<CarTypeEntity>();
        public DbSet<Reservation> Reservations => Set<Reservation>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    }

    public class CarTypeEntity
    {
        public string TypeName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<Car> Cars { get; set; } = new();
    }

    public class Car
    {
        public string CarId { get; set; } = string.Empty;
        public string CarTypeName { get; set; } = string.Empty;
        public CarTypeEntity CarType { get; set; } = null!;
        
        [Timestamp]
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
        public List<Reservation> Reservations { get; set; } = new();
    }

    public class Reservation
    {
        public string ReservationId { get; set; } = string.Empty;
        public string CarId { get; set; } = string.Empty;
        public Car Car { get; set; } = null!;
        public Guid IdempotencyKey { get; set; }
        public DateTime StartDate { get; set; }
        public int Days { get; set; }
        public DateTime EndDate { get; set; }
    }

    public class OutboxMessage
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime OccurredOnUtc { get; set; }
        public DateTime? ProcessedOnUtc { get; set; }
        public string? Error { get; set; }
    }
}
