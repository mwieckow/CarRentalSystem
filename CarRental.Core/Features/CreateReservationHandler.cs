using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CarRental.Core.Features
{
    public class CreateReservationHandler : IRequestHandler<CreateReservationCommand, ReservationDto?>
    {
        private readonly RentalDbContext _context;
        private readonly ILogger<CreateReservationHandler> _logger;

        public CreateReservationHandler(RentalDbContext context, ILogger<CreateReservationHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ReservationDto?> Handle(CreateReservationCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing reservation command (CQRS): {@Command}", request);

            var existingReservation = await _context.Reservations
                .Include(r => r.Car)
                .FirstOrDefaultAsync(r => r.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            if (existingReservation != null)
            {
                _logger.LogInformation("Returning existing reservation: {ReservationId}", existingReservation.ReservationId);
                return new ReservationDto(existingReservation.ReservationId, existingReservation.CarId, existingReservation.Car.CarTypeName, existingReservation.StartDate, existingReservation.EndDate);
            }

            DateTime endDate = request.StartDate.AddDays(request.Days);
            var availableCar = await _context.Cars
                .Include(c => c.Reservations)
                .Where(c => c.CarTypeName == request.CarType)
                .FirstOrDefaultAsync(car => !car.Reservations.Any(res => request.StartDate < res.EndDate && endDate > res.StartDate), cancellationToken);

            if (availableCar == null)
            {
                _logger.LogWarning("No vehicles found for type {CarType}", request.CarType);
                return null;
            }

            string resId = $"RES-{(await _context.Reservations.CountAsync(cancellationToken) + 1):D4}";
            var reservation = new Reservation
            {
                ReservationId = resId,
                CarId = availableCar.CarId,
                StartDate = request.StartDate,
                Days = request.Days,
                EndDate = endDate,
                IdempotencyKey = request.IdempotencyKey
            };

            _context.Reservations.Add(reservation);

            _context.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "ReservationConfirmed",
                Content = JsonSerializer.Serialize(new { ReservationId = resId, Email = "customer@example.com" }),
                OccurredOnUtc = DateTime.UtcNow
            });

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Saved new reservation: {ReservationId}", resId);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("Concurrency conflict (Optimistic Lock fail) for car {CarId}", availableCar.CarId);
                return null;
            }

            return new ReservationDto(resId, availableCar.CarId, availableCar.CarTypeName, reservation.StartDate, reservation.EndDate);
        }
    }
}
