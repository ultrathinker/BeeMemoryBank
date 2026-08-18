using Dapper;
using System.Data;
using System.Globalization;

namespace BeeMemoryBank.Storage.Sqlite;

public static class DapperConfig
{
    private static bool _configured;
    private static readonly object Lock = new();

    public static void Configure()
    {
        lock (Lock)
        {
            if (_configured) return;
            SqlMapper.AddTypeHandler(new GuidTypeHandler());
            SqlMapper.AddTypeHandler(new NullableGuidTypeHandler());
            SqlMapper.AddTypeHandler(new DateTimeTypeHandler());
            SqlMapper.AddTypeHandler(new NullableDateTimeTypeHandler());
            _configured = true;
        }
    }

    private sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
            => parameter.Value = value.ToString();

        public override Guid Parse(object value)
            => Guid.Parse((string)value);
    }

    private sealed class NullableGuidTypeHandler : SqlMapper.TypeHandler<Guid?>
    {
        public override void SetValue(IDbDataParameter parameter, Guid? value)
            => parameter.Value = value?.ToString() ?? (object)DBNull.Value;

        public override Guid? Parse(object value)
            => value is null or DBNull ? null : Guid.Parse((string)value);
    }

    private sealed class DateTimeTypeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
            => parameter.Value = value.ToString("o");

        public override DateTime Parse(object value)
        {
            var dt = DateTime.Parse((string)value, null, DateTimeStyles.RoundtripKind);
            // Legacy/imported rows can lack a zone marker ("Unspecified" kind); every value this
            // app ever writes is UTC, so treat an unmarked value as UTC rather than leaving it
            // ambiguous -- otherwise JSON serialization silently omits the offset/'Z' suffix.
            return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
        }
    }

    private sealed class NullableDateTimeTypeHandler : SqlMapper.TypeHandler<DateTime?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime? value)
            => parameter.Value = value?.ToString("o") ?? (object)DBNull.Value;

        public override DateTime? Parse(object value)
        {
            if (value is null or DBNull) return null;
            var dt = DateTime.Parse((string)value, null, DateTimeStyles.RoundtripKind);
            return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
        }
    }
}
