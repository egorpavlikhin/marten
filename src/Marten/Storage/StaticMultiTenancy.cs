using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline.ImTools;
using Marten.Schema;
using Npgsql;
using Weasel.Core.Migrations;

namespace Marten.Storage
{
    public interface IStaticMultiTenancy
    {
        /// <summary>
        /// Register a database that will hold data for multiple conjoined tenants
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseIdentifier">A descriptive name for this database. If omitted, this will be derived from the connection string</param>
        /// <returns></returns>
        IDatabaseExpression AddMultipleTenantDatabase(string connectionString, string databaseIdentifier = null);

        void AddSingleTenantDatabase(string connectionString, string tenantId);
    }

    public class StaticMultiTenancy: Tenancy, ITenancy, IStaticMultiTenancy
    {
        private ImHashMap<string, Tenant> _tenants = ImHashMap<string, Tenant>.Empty;
        private ImHashMap<string, MartenDatabase> _databases = ImHashMap<string, MartenDatabase>.Empty;

        public StaticMultiTenancy(StoreOptions options) : base(options)
        {
            Cleaner = new CompositeDocumentCleaner(this);
        }

        /// <summary>
        /// Register a database that will hold data for multiple conjoined tenants
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseIdentifier">A descriptive name for this database. If omitted, this will be derived from the connection string</param>
        /// <returns></returns>
        public IDatabaseExpression AddMultipleTenantDatabase(string connectionString, string databaseIdentifier = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var identifier = databaseIdentifier ?? $"{builder.Database}@{builder.Host}";

            var database = new MartenDatabase(Options, new ConnectionFactory(connectionString), identifier);
            _databases = _databases.AddOrUpdate(identifier, database);

            return new DatabaseExpression(this, database);
        }

        public void AddSingleTenantDatabase(string connectionString, string tenantId)
        {
            var database = new MartenDatabase(Options, new ConnectionFactory(connectionString), tenantId);
            _databases = _databases.AddOrUpdate(tenantId, database);

            var expression = new DatabaseExpression(this, database).ForTenants(tenantId);

            if (Default == null)
            {
                expression.AsDefault();
            }
        }

        public class DatabaseExpression: IDatabaseExpression
        {
            private readonly StaticMultiTenancy _parent;
            private readonly MartenDatabase _database;

            internal DatabaseExpression(StaticMultiTenancy parent, MartenDatabase database)
            {
                _parent = parent;
                _database = database;
            }

            /// <summary>
            /// Tells Marten that the designated tenant ids are stored in the current database
            /// </summary>
            /// <param name="tenantIds"></param>
            /// <returns></returns>
            public DatabaseExpression ForTenants(params string[] tenantIds)
            {
                foreach (var tenantId in tenantIds)
                {
                    var tenant = new Tenant(tenantId, _database);
                    _parent._tenants = _parent._tenants.AddOrUpdate(tenantId, tenant);
                }

                return this;
            }

            public DatabaseExpression AsDefault()
            {
                _parent.Default = new Tenant(DefaultTenantId, _database);
                return this;
            }
        }

        public ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
        {
            var databases = _databases.Enumerate().Select(x => x.Value).ToList();
            return new ValueTask<IReadOnlyList<IDatabase>>(databases);
        }

        public Tenant GetTenant(string tenantId)
        {
            if (_tenants.TryFind(tenantId, out var tenant))
            {
                return tenant;
            }

            throw new UnknownTenantIdException(tenantId);
        }

        public Tenant Default { get; private set; }
        public IDocumentCleaner Cleaner { get; }
        public ValueTask<Tenant> GetTenantAsync(string tenantId)
        {
            return new ValueTask<Tenant>(GetTenant(tenantId));
        }

        public ValueTask<IMartenDatabase> FindOrCreateDatabase(string tenantIdOrDatabaseIdentifier)
        {
            if (_databases.TryFind(tenantIdOrDatabaseIdentifier, out var database))
            {
                return new ValueTask<IMartenDatabase>(database);
            }

            if (_tenants.TryFind(tenantIdOrDatabaseIdentifier, out var tenant))
            {
                return new ValueTask<IMartenDatabase>(tenant.Database);
            }

            throw new UnknownTenantIdException(tenantIdOrDatabaseIdentifier);
        }
    }

    public interface IDatabaseExpression
    {
        /// <summary>
        /// Tells Marten that the designated tenant ids are stored in the current database
        /// </summary>
        /// <param name="tenantIds"></param>
        /// <returns></returns>
        StaticMultiTenancy.DatabaseExpression ForTenants(params string[] tenantIds);
    }
}
