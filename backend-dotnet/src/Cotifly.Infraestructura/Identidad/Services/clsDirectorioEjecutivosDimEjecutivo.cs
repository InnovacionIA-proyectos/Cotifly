using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// Adaptador de <see cref="IClsDirectorioEjecutivos"/> sobre la tabla de ejecutivos de <c>DWCubosEBSSAP</c>
/// (<c>ConnectionStrings:Regional</c>, sección <see cref="clsOpcionesRegional"/>; ADR-016 §6). Solo <c>SELECT</c> parametrizado;
/// esquema, tabla y columnas vienen de la configuración, validados como identificadores y citados con corchetes. Los tipos
/// reales de las columnas no se conocen: se leen como texto (<c>CAST ... AS nvarchar</c>), recortados y en mayúsculas para
/// comparar. Los fallos de la base se propagan (quien llama responde 503).
/// </summary>
public sealed class clsDirectorioEjecutivosDimEjecutivo(
    IOptions<clsOpcionesConexiones> conexiones,
    IOptions<clsOpcionesRegional> opciones,
    ILogger<clsDirectorioEjecutivosDimEjecutivo> logger) : IClsDirectorioEjecutivos
{
    /// <summary>Parámetros por consulta (SQL Server admite 2100; se deja margen).</summary>
    public const int TamanoLote = 500;

    public bool Configurado => !string.IsNullOrWhiteSpace(conexiones.Value.Regional);

    public async Task<IReadOnlyDictionary<string, string>> ObtenerRegionalesAsync(
        IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nombresUsuario);
        var nombres = nombresUsuario.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (nombres.Count == 0)
        {
            return resultado;
        }

        var filas = new List<(string Usuario, string? Regional)>();
        await using var conexion = await AbrirAsync(cancellationToken);
        foreach (var lote in nombres.Chunk(TamanoLote))
        {
            await using var comando = conexion.CreateCommand();
            var parametros = new List<string>(lote.Length);
            for (var i = 0; i < lote.Length; i++)
            {
                var nombre = $"@u{i}";
                parametros.Add(nombre);
                comando.Parameters.Add(new SqlParameter(nombre, System.Data.SqlDbType.NVarChar, 255) { Value = lote[i].ToUpperInvariant() });
            }

            comando.CommandText = $"""
                SELECT {Texto(opciones.Value.ColumnaUsuario)} AS usuario, {Texto(opciones.Value.ColumnaRegional)} AS regional
                FROM {Tabla()}
                WHERE UPPER({Texto(opciones.Value.ColumnaUsuario)}) IN ({string.Join(", ", parametros)});
                """;
            comando.CommandTimeout = opciones.Value.TiempoEsperaSegundos;
            await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
            while (await lector.ReadAsync(cancellationToken))
            {
                filas.Add((lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetString(1)));
            }
        }

        foreach (var grupo in filas.GroupBy(f => f.Usuario, StringComparer.OrdinalIgnoreCase))
        {
            var regionales = grupo.Select(f => f.Regional).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (regionales.Count == 1)
            {
                resultado[grupo.Key] = regionales[0];
            }
            else if (regionales.Count > 1)
            {
                logger.LogWarning("El usuario '{Usuario}' tiene {Cantidad} regionales distintas en la tabla de ejecutivos: se ignora.", grupo.Key, regionales.Count);
            }
        }

        return resultado;
    }

    public async Task<IReadOnlyList<string>> ListarNombresUsuarioAsync(string regional, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regional);
        await using var conexion = await AbrirAsync(cancellationToken);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT DISTINCT {Texto(opciones.Value.ColumnaUsuario)} AS usuario
            FROM {Tabla()}
            WHERE UPPER({Texto(opciones.Value.ColumnaRegional)}) = @regional
              AND {Texto(opciones.Value.ColumnaUsuario)} IS NOT NULL;
            """;
        comando.Parameters.Add(new SqlParameter("@regional", System.Data.SqlDbType.NVarChar, 255) { Value = regional.Trim().ToUpperInvariant() });
        comando.CommandTimeout = opciones.Value.TiempoEsperaSegundos;

        var nombres = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        while (await lector.ReadAsync(cancellationToken))
        {
            if (!lector.IsDBNull(0) && lector.GetString(0) is { Length: > 0 } nombre)
            {
                nombres.Add(nombre);
            }
        }

        return nombres;
    }

    private async Task<SqlConnection> AbrirAsync(CancellationToken cancellationToken)
    {
        var cadena = conexiones.Value.Regional;
        if (string.IsNullOrWhiteSpace(cadena))
        {
            throw new InvalidOperationException("ConnectionStrings:Regional no está configurada.");
        }

        var conexion = new SqlConnection(cadena);
        try
        {
            await conexion.OpenAsync(cancellationToken);
            return conexion;
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }
    }

    /// <summary><c>[esquema].[tabla]</c> (los identificadores ya pasaron la validación de <see cref="clsOpcionesRegional"/>).</summary>
    private string Tabla() => $"[{opciones.Value.Esquema}].[{opciones.Value.Tabla}]";

    /// <summary>Columna leída como texto recortado.</summary>
    private static string Texto(string columna) => $"LTRIM(RTRIM(CAST([{columna}] AS nvarchar(255))))";
}
