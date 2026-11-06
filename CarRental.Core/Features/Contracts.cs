using System;
using System.Collections.Generic;
using MediatR;

namespace CarRental.Core.Features
{
    public record GetAvailableCarTypesQuery(DateTime StartDate, int Days) : IRequest<List<CarTypeDto>>;
    public record CreateReservationCommand(string CarType, DateTime StartDate, int Days, Guid IdempotencyKey) : IRequest<ReservationDto?>;

    public record CarTypeDto(string TypeName, string Description, int AvailableCount);
    public record ReservationDto(string ReservationId, string CarId, string CarType, DateTime StartDate, DateTime EndDate);
}
