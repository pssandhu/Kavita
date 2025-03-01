﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using API.Entities.Enums;
using CsvHelper;
using Kavita.Common.EnvironmentInfo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace API.Data.ManualMigrations;

internal sealed class SeriesRelationMigrationOutput
{
    public required string SeriesName { get; set; }
    public int SeriesId { get; set; }
    public required string TargetSeriesName { get; set; }
    public int TargetId { get; set; }
    public RelationKind Relationship { get; set; }
}

/// <summary>
/// Introduced in v0.6.1.2 and v0.7, this exports to a temp file the existing series relationships. It is a 3 part migration.
/// This will run first, to export the data, then the DB migration will change the way the DB is constructed, then the last migration
/// will import said file and re-construct the relationships.
/// </summary>
public static class MigrateSeriesRelationsExport
{
    private const string OutputFile = "config/relations.csv";
    private const string CompleteOutputFile = "config/relations-imported.csv";
    public static async Task Migrate(DataContext dataContext, ILogger<Program> logger)
    {
        logger.LogCritical("Running MigrateSeriesRelationsExport migration - Please be patient, this may take some time. This is not an error");
        if (BuildInfo.Version > new Version(0, 6, 1, 3)
            || new FileInfo(OutputFile).Exists
            || new FileInfo(CompleteOutputFile).Exists)
        {
            logger.LogCritical("Running MigrateSeriesRelationsExport migration - complete. Nothing to do");
            return;
        }

        var seriesWithRelationships = await dataContext.Series
            .Where(s => s.Relations.Any())
            .Include(s => s.Relations)
            .ThenInclude(r => r.TargetSeries)
            .ToListAsync();

        var records = new List<SeriesRelationMigrationOutput>();
        var excludedRelationships = new List<RelationKind>()
        {
            RelationKind.Parent,
        };
        foreach (var series in seriesWithRelationships)
        {
            foreach (var relationship in series.Relations.Where(r => !excludedRelationships.Contains(r.RelationKind)))
            {
                records.Add(new SeriesRelationMigrationOutput()
                {
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Relationship = relationship.RelationKind,
                    TargetId = relationship.TargetSeriesId,
                    TargetSeriesName = relationship.TargetSeries.Name
                });
            }
        }

        await using var writer = new StreamWriter(OutputFile);
        await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            await csv.WriteRecordsAsync(records);
        }

        await writer.DisposeAsync();

        logger.LogCritical("{OutputFile} has a backup of all data", OutputFile);

        logger.LogCritical("Deleting all relationships in the DB. This is not an error");
        var entities = await dataContext.SeriesRelation
            .Include(s => s.Series)
            .Include(s => s.TargetSeries)
            .Select(s => s)
            .ToListAsync();

        foreach (var seriesWithRelationship in entities)
        {
            logger.LogCritical("Deleting {SeriesName} --{RelationshipKind}--> {TargetSeriesName}",
                seriesWithRelationship.Series.Name, seriesWithRelationship.RelationKind, seriesWithRelationship.TargetSeries.Name);
            dataContext.SeriesRelation.Remove(seriesWithRelationship);

            await dataContext.SaveChangesAsync();
        }

        // In case of corrupted entities (where series were deleted but their Id still existed, we delete the rest of the table)
        dataContext.SeriesRelation.RemoveRange(dataContext.SeriesRelation);
        await dataContext.SaveChangesAsync();


        logger.LogCritical("Running MigrateSeriesRelationsExport migration - Completed. This is not an error");
    }
}
