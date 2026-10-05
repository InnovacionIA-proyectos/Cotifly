using Microsoft.Data.SqlClient;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Base auxiliar de una sola prueba (nombre único, creada con <see cref="clsBaseDatosPruebas.CrearBaseConScriptsAsync"/>). Se usa
/// con <c>await using</c>: al salir, también si la prueba falla, cierra las conexiones abiertas
/// (<c>SINGLE_USER WITH ROLLBACK IMMEDIATE</c>) y la borra, para no acumular bases en el SQL Server compartido (#91).
/// </summary>
public sealed class clsBaseAuxiliarPruebas : IAsyncDisposable
{
    private readonly string _cadenaMaster;

    internal clsBaseAuxiliarPruebas(string cadenaMaster, string nombre, string cadena)
    {
        _cadenaMaster = cadenaMaster;
        Nombre = nombre;
        Cadena = cadena;
    }

    /// <summary>Nombre de la base en el contenedor.</summary>
    public string Nombre { get; }

    /// <summary>Cadena de conexión a la base auxiliar.</summary>
    public string Cadena { get; }

    public async ValueTask DisposeAsync()
    {
        // Las conexiones del pool de esta cadena quedarían apuntando a una base borrada.
        using (var conexion = new SqlConnection(Cadena))
        {
            SqlConnection.ClearPool(conexion);
        }

        await clsBaseDatosPruebas.EjecutarAsync(_cadenaMaster, $"""
            IF DB_ID(N'{Nombre}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Nombre}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Nombre}];
            END
            """);
    }
}
