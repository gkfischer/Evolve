﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Evolve.Configuration;
using Evolve.Connection;
using Evolve.Dialect;
using Evolve.Metadata;
using Evolve.Migration;
using Evolve.Utilities;

namespace Evolve
{
    public class Evolve : IEvolveConfiguration
    {
        #region Constants

        // Initialize
        private const string EvolveInitialized = "Evolve initialized.";

        // Validate
        private const string IncorrectMigrationChecksum = "Validate failed: invalid checksum for migration: {0}.";
        private const string MigrationMetadataNotFound = "Validate failed: script {0} not found in the metadata table of applied migrations.";
        private const string ChecksumFixed = "Checksum fixed for migration: {0}.";
        private const string NoMetadataFound = "No metadata found.";
        private const string ValidateSuccessfull = "Metadata validated.";

        // ManageSchemas
        private const string NewSchemaCreated = "Create new schema: {0}.";
        private const string EmptySchemaFound = "Empty schema found: {0}.";
        private const string SchemaNotExists = "Schema {0} does not exist.";
        private const string SchemaCreated = "Schema {0} created.";
        private const string SchemaMarkedEmpty = "Mark schema {0} as empty.";

        // Migrate
        private const string ExecutingMigrate = "Executing Migrate...";
        private const string MigrationError = "Error executing script: {0}.";
        private const string MigrationErrorEraseOnValidationError = "{0} Erase database. (MustEraseOnValidationError = True)";
        private const string MigrationSuccessfull = "Successfully applied migration {0}.";
        private const string NothingToMigrate = "Database is up to date. No migration needed.";
        private const string MigrateSuccessfull = "Database migrated to version {0}. {1} migration(s) applied.";

        // Erase
        private const string ExecutingErase = "Executing Erase...";
        private const string EraseDisabled = "Erase is disabled.";
        private const string EraseSchemaSuccessfull = "Successfully erased schema {0}.";
        private const string DropSchemaSuccessfull = "Successfully dropped schema {0}.";
        private const string EraseSchemaFailed = "Erase failed. Impossible to erase schema {0}.";
        private const string DropSchemaFailed = "Erase failed. Impossible to drop schema {0}.";
        private const string EraseCompleted = "Erase completed.";

        // Repair
        private const string ExecutingRepair = "Executing Repair...";
        private const string RepairSuccessfull = "Successfully repaired {0} migration(s).";
        private const string NothingToRepair = "Metadata are up to date. Nothing to repair.";

        #endregion

        #region Fields

        private string _configurationPath;
        private IDbConnection _userDbConnection;
        private IMigrationLoader _loader = new FileMigrationLoader();
        private Action<string> _logInfoDelegate;

        #endregion

        #region Constructor

        /// <summary>
        ///     <para>
        ///         Constructor.
        ///     </para>
        ///     <para>
        ///         Set the default configuration values.
        ///     </para>
        ///     <para>
        ///         Load the configuration file at <paramref name="evolveConfigurationPath"/>.
        ///     </para>
        /// </summary>
        /// <param name="evolveConfigurationPath">Evolve configuration file.</param>
        /// <param name="dbConnection">Optionnal database connection.</param>
        /// <param name="logInfoDelegate">Optionnal logger.</param>
        public Evolve(string evolveConfigurationPath, IDbConnection dbConnection = null, Action<string> logInfoDelegate = null)
        {
            _configurationPath = Check.FileExists(evolveConfigurationPath, nameof(evolveConfigurationPath));
            _userDbConnection = dbConnection;
            _logInfoDelegate = logInfoDelegate ?? new Action<string>((msg) => { });

            // Set default configuration settings
            Command = CommandOptions.Migrate;
            Schemas = new List<string>();
            Encoding = Encoding.UTF8;
            Locations = new List<string> { "Sql_Scripts" };
            MetadaTableName = "changelog";
            PlaceholderPrefix = "${";
            PlaceholderSuffix = "}";
            Placeholders = new Dictionary<string, string>();
            SqlMigrationPrefix = "V";
            SqlMigrationSeparator = "__";
            SqlMigrationSuffix = ".sql";
            TargetVersion = new MigrationVersion(long.MaxValue.ToString());

            // Configure Evolve
            var configurationProvider = ConfigurationFactoryProvider.GetProvider(evolveConfigurationPath);
            configurationProvider.Configure(evolveConfigurationPath, this);
        }

        #endregion

        #region IEvolveConfiguration

        public string ConnectionString { get; set; }
        public IEnumerable<string> Schemas { get; set; }
        public string Driver { get; set; }
        public CommandOptions Command { get; set; }
        public bool IsEraseDisabled { get; set; }
        public bool MustEraseOnValidationError { get; set; }
        public Encoding Encoding { get; set; }
        public IEnumerable<string> Locations { get; set; }
        public string MetadaTableName { get; set; }

        private string _metadaTableSchema;
        public string MetadataTableSchema
        {
            get => _metadaTableSchema.IsNullOrWhiteSpace() ? Schemas?.FirstOrDefault() : _metadaTableSchema;
            set => _metadaTableSchema = value;
        }

        public string PlaceholderPrefix { get; set; }
        public string PlaceholderSuffix { get; set; }
        public Dictionary<string, string> Placeholders { get; set; }
        public string SqlMigrationPrefix { get; set; }
        public string SqlMigrationSeparator { get; set; }
        public string SqlMigrationSuffix { get; set; }
        public MigrationVersion TargetVersion { get; set; }

        #endregion

        #region Properties

        public int NbMigration { get; private set; }
        public int NbReparation { get; private set; }

        #endregion

        #region Commands

        public void ExecuteCommand()
        {
            switch (Command)
            {
                case CommandOptions.Migrate:
                    Migrate();
                    break;
                case CommandOptions.Repair:
                    Repair();
                    break;
                case CommandOptions.Erase:
                    Erase();
                    break;
                default:
                    Migrate();
                    break;
            }
        }

        public string GenerateScript(string fromMigration = null, string toMigration = null)
        {
            throw new NotImplementedException();
        }

        public void Migrate()
        {
            _logInfoDelegate(ExecutingMigrate);

            var db = Initialize();

            try
            {
                Validate(db);
            }
            catch (EvolveValidationException ex)
            {
                if (MustEraseOnValidationError)
                {
                    _logInfoDelegate(string.Format(MigrationErrorEraseOnValidationError, ex.Message));

                    Erase();
                }
                else
                {
                    throw ex;
                }
            }

            ManageSchemas(db);

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadaTableName);
            var lastAppliedVersion = metadata.GetAllMigrationMetadata().LastOrDefault()?.Version ?? new MigrationVersion("0");
            var scripts = _loader.GetMigrations(Locations, SqlMigrationPrefix, SqlMigrationSeparator, SqlMigrationSuffix)
                                 .SkipWhile(x => x.Version <= lastAppliedVersion)
                                 .TakeWhile(x => x.Version <= TargetVersion);

            foreach (var script in scripts)
            {
                try
                {
                    db.WrappedConnection.BeginTransaction();
                    db.WrappedConnection.ExecuteNonQuery(script.LoadSQL(Placeholders, Encoding));
                    metadata.SaveMigration(script, true);
                    db.WrappedConnection.Commit();
                    NbMigration++;

                    _logInfoDelegate(string.Format(MigrationSuccessfull, script.Name));
                }
                catch(Exception ex)
                {
                    db.WrappedConnection.Rollback();
                    metadata.SaveMigration(script, false);
                    throw new EvolveException(string.Format(MigrationError, script.Name), ex);
                }
            }

            if(NbMigration == 0)
            {
                _logInfoDelegate(NothingToMigrate);
            }
            else
            {
                _logInfoDelegate(string.Format(MigrateSuccessfull, scripts.Last().Version, NbMigration));
            }
        }

        public void Erase()
        {
            _logInfoDelegate(ExecutingErase);

            if(IsEraseDisabled)
            {
                _logInfoDelegate(EraseDisabled);
                return;
            }

            var db = Initialize();
            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadaTableName);

            db.WrappedConnection.BeginTransaction();

            foreach (var schemaName in FindSchemas())
            {
                if (metadata.CanDropSchema(schemaName))
                {
                    try
                    {
                        db.GetSchema(schemaName).Drop();
                        _logInfoDelegate(string.Format(DropSchemaSuccessfull, schemaName));
                    }
                    catch (Exception ex)
                    {
                        throw new EvolveException(string.Format(DropSchemaFailed, schemaName), ex);
                    }
                }
                else if (metadata.CanEraseSchema(schemaName))
                {
                    try
                    {
                        db.GetSchema(schemaName).Erase();
                        _logInfoDelegate(string.Format(EraseSchemaSuccessfull, schemaName));
                    }
                    catch (Exception ex)
                    {
                        throw new EvolveException(string.Format(EraseSchemaFailed, schemaName), ex);
                    }
                }
            }

            db.WrappedConnection.Commit();

            _logInfoDelegate(EraseCompleted);
        }

        public void Repair()
        {
            _logInfoDelegate(ExecutingRepair);

            var db = Initialize();
            Validate(db);

            if (NbReparation == 0)
            {
                _logInfoDelegate(NothingToRepair);
            }
            else
            {
                _logInfoDelegate(string.Format(RepairSuccessfull, NbReparation));
            }
        }

        #endregion

        private DatabaseHelper Initialize()
        {
            NbMigration = 0;
            NbReparation = 0;

            var connectionProvider = GetConnectionProvider(_userDbConnection);              // Get a database connection provider
            var evolveConnection = connectionProvider.GetConnection();                      // Get a connection to the database
            evolveConnection.Validate();                                                    // Validate the reliabilty of the initiated connection
            var dbmsType = evolveConnection.GetDatabaseServerType();                        // Get the DBMS type
            var db = DatabaseHelperFactory.GetDatabaseHelper(dbmsType, evolveConnection);   // Get the DatabaseHelper
            if(Schemas == null || Schemas.Count() == 0)
            {
                Schemas = new List<string> { db.GetCurrentSchemaName() };                   // If no schema, get the one associated to the datasource connection
            }

            _logInfoDelegate(EvolveInitialized);

            return db;
        }

        private void Validate(DatabaseHelper db)
        {
            Check.NotNull(db, nameof(db));

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadaTableName);                                       // Get the metadata table
            if (!metadata.IsExists())
            {
                _logInfoDelegate(NoMetadataFound); // Nothing to validate
                return;
            }

            var appliedMigrations = metadata.GetAllMigrationMetadata();                                                     // Load all applied migrations metadata
            if (appliedMigrations.Count() == 0)                                                                             
            {
                _logInfoDelegate(NoMetadataFound); // Nothing to validate
                return;
            }

            var lastAppliedVersion = appliedMigrations.Last().Version;                                                      // Get the last applied migration version
            var startVersion = metadata.FindStartVersion();                                                                 // Load start version from metadata
            var scripts = _loader.GetMigrations(Locations, SqlMigrationPrefix, SqlMigrationSeparator, SqlMigrationSuffix)
                                 .SkipWhile(x => x.Version < startVersion)                                         
                                 .TakeWhile(x => x.Version <= lastAppliedVersion);                                          // Keep scripts between first and last applied migration

            foreach (var script in scripts)
            {
                var appliedMigration = appliedMigrations.SingleOrDefault(x => x.Version == script.Version);                 // Search script in the applied migrations
                if (appliedMigration == null)
                {
                    throw new EvolveValidationException(string.Format(MigrationMetadataNotFound, script.Name));                       
                }

                string scriptChecksum = script.CalculateChecksum();
                if (scriptChecksum != appliedMigration.Checksum)                                                            // Script found, verify checksum
                {
                    if (Command == CommandOptions.Repair)
                    {
                        metadata.UpdateChecksum(appliedMigration.Id, scriptChecksum);
                        NbReparation++;

                        _logInfoDelegate(string.Format(ChecksumFixed, script.Name));
                    }
                    else
                    {
                        throw new EvolveValidationException(string.Format(IncorrectMigrationChecksum, script.Name));
                    }
                }
            }

            _logInfoDelegate(ValidateSuccessfull);
        }

        private void ManageSchemas(DatabaseHelper db)
        {
            Check.NotNull(db, nameof(db));

            var metadata = db.GetMetadataTable(MetadataTableSchema, MetadaTableName);

            foreach (var schemaName in FindSchemas())
            {
                var schema = db.GetSchema(schemaName);

                if(!schema.IsExists())
                {
                    _logInfoDelegate(string.Format(SchemaNotExists, schemaName));

                    // Create new schema
                    db.WrappedConnection.BeginTransaction();
                    schema.Create();
                    metadata.Save(MetadataType.NewSchema, "0", string.Format(NewSchemaCreated, schemaName), schemaName);
                    db.WrappedConnection.Commit();

                    _logInfoDelegate(string.Format(SchemaCreated, schemaName));
                }
                else if(schema.IsEmpty())
                {
                    // Mark schema as empty in the metadata table
                    metadata.Save(MetadataType.EmptySchema, "0", string.Format(EmptySchemaFound, schemaName), schemaName);

                    _logInfoDelegate(string.Format(SchemaMarkedEmpty, schemaName));
                }
            }
        }

        private IConnectionProvider GetConnectionProvider(IDbConnection connection = null)
        {
            return connection != null ? new ConnectionProvider(connection) as IConnectionProvider
                                      : new DriverConnectionProvider(Driver, ConnectionString);
        }

        private IEnumerable<string> FindSchemas()
        {
            return new List<string>().Union(Schemas ?? new List<string>())
                                     .Union(new List<string> { MetadataTableSchema })
                                     .Where(s => !s.IsNullOrWhiteSpace())
                                     .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}

