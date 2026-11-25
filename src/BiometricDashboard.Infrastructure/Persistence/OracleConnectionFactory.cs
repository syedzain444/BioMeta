using Microsoft.Extensions.Configuration;



namespace BiometricDashboard.Infrastructure.Persistence;



public sealed class OracleConnectionFactory

{

    private readonly string _connectionString;



    public OracleConnectionFactory(IConfiguration configuration)

    {

        _connectionString = configuration.GetConnectionString("GateEntryOracle")

            ?? throw new InvalidOperationException("ConnectionStrings:GateEntryOracle is required (Gate_Entry_Surveillance / entrygate_surv)");

    }



    public Oracle.ManagedDataAccess.Client.OracleConnection Create() => new(_connectionString);

}


