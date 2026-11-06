using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MediatR;

namespace CarRental.Core.Features
{
    public class GetAvailableCarTypesHandler : IRequestHandler<GetAvailableCarTypesQuery, List<CarTypeDto>>
    {
        private readonly RentalDbContext _context;

        public GetAvailableCarTypesHandler(RentalDbContext context) => _context = context;

        public async Task<List<CarTypeDto>> Handle(GetAvailableCarTypesQuery request, CancellationToken cancellationToken)
        {
            if (request.Days <= 0) return new List<CarTypeDto>();
            DateTime endDate = request.StartDate.AddDays(request.Days);

            return await _context.CarTypes
                .AsNoTracking()
                .Select(ct => new CarTypeDto(
                    ct.TypeName,
                    ct.Description,
                    ct.Cars.Count(car => !car.Reservations.Any(res => request.StartDate < res.EndDate && endDate > res.StartDate))
                ))
                .ToListAsync(cancellationToken);
        }
    }
}
