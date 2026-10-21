using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Turno.Infrastructure.Persistence;

public static class SnakeCaseNaming
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var entities = modelBuilder.Model.GetEntityTypes().ToList();

        foreach (var entity in entities)
        {
            if (entity.GetTableName() is { } table)
            {
                entity.SetTableName(ToSnakeCase(table));
            }
        }

        foreach (var entity in entities)
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName() is { } column)
                {
                    property.SetColumnName(ToSnakeCase(column));
                }
            }
        }

        foreach (var entity in entities)
        {
            foreach (var key in entity.GetKeys())
            {
                if (key.GetName() is { } name)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (foreignKey.GetConstraintName() is { } name)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                var startsWord = i > 0 && previous != '_' &&
                    (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next)));
                if (startsWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
