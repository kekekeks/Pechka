using System;
using FluentMigrator;

namespace Pechka.AspNet.Database
{
    public class MigrationDateAttribute : MigrationAttribute
    {
        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public MigrationDateAttribute(int year, int month, int day, int hour, int minute, int second = 0)
            : base(Convert(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)))
        {
        }

        private static long Convert(DateTime dt)
        {
            return (long)(dt - UnixEpoch).TotalSeconds;
        }
    }
}
