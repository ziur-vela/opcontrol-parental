// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpControl.Parental.Server.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ParentalDbContext>
{
    public ParentalDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=parental;Username=parental;Password=design-time";
        var options = new DbContextOptionsBuilder<ParentalDbContext>()
            .UseNpgsql(connection)
            .Options;
        return new ParentalDbContext(options);
    }
}
