using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// Adaptador de <see cref="IClsDirectorioEjecutivos"/> sobre la tabla de ejecutivos de <c>DWCubosEBSSAP</c>
/// (<c>ConnectionStrings:Regional</c>, sección <see cref="clsOpcionesRegional"/>; ADR-016 §6). Solo <c>SELECT</c> parametrizado;
/// esquema, tabla y columnas vienen de la configuración, validados como identificadores y citados con corchetes. Las columnas
/// se leen como texto (<c>CAST ... AS nvarchar</c>), recortadas y en mayúsculas para comparar; a la de usuario (<c>USUARIO</c>,
/// <c>boterosoto\usuario</c>) se le quita el prefijo de dominio (#81). Los fallos de la base se propagan (el tablero responde
/// 503; el login sigue sin refrescar) y no se guardan; las respuestas exitosas se reutilizan 60 s
/// (<see cref="clsCacheDirectorioEjecutivos"/>).
/// </summary>
public sealed class clsDirectorioEjecutivosDimEjecutivo(
    IOptions<clsOpcionesConexiones> conexiones,
    IOptions<clsOpcionesRegional> opciones,
    clsCacheDirectorioEjecutivos cache,
    ILogger<clsDirectorioEjecutivosDimEjecutivo> logger) : IClsDirectorioEjecutivos
{
    /// <summary>Parámetros por consulta (SQL Server admite 2100; se deja margen).</summary>
    public const int TamanoLote = 500;

    public bool Configurado => !string.IsNullOrWhiteSpace(conexiones.Value.Regional);

    public TimeSpan LimiteLogin => opciones.Value.LimiteLogin;

    public async Task<IReadOnlyDictionary<string, string>> ObtenerRegionalesAsync(
        IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nombresUsuario);
        var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendientes = new List<string>();
        foreach (var nombre in nombresUsuario.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cache.IntentarUsuario(nombre, out var enCache))
            {
                if (enCache is not null)
                {
                    resultado[nombre] = enCache;
                }
            }
            else
            {
                pendientes.Add(nombre);
            }
        }

        if (pendientes.Count == 0)
        {
            return resultado;
        }

        var filas = new List<(string Usuario, string? Regional)>();
        await using (var conexion = await AbrirAsync(cancellationToken))
        {
            foreach (var lote in pendientes.Chunk(TamanoLote))
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
                    SELECT {SinDominio(opciones.Value.ColumnaUsuario)} AS usuario, {Texto(opciones.Value.ColumnaRegional)} AS regional
                    FROM {Tabla()}
                    WHERE UPPER({SinDominio(opciones.Value.ColumnaUsuario)}) IN ({string.Join(", ", parametros)});
                    """;
                comando.CommandTimeout = opciones.Value.TiempoEsperaSegundos;
                await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
                while (await lector.ReadAsync(cancellationToken))
                {
                    filas.Add((lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetString(1)));
                }
            }
        }

        var encontrados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grupo in filas.GroupBy(f => f.Usuario, StringComparer.OrdinalIgnoreCase))
        {
            var regionales = grupo.Select(f => f.Regional).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (regionales.Count == 1)
            {
                encontrados[grupo.Key] = regionales[0];
            }
            else if (regionales.Count > 1)
            {
                logger.LogWarning("El usuario '{Usuario}' tiene {Cantidad} regionales distintas en la tabla de ejecutivos: se ignora.", grupo.Key, regionales.Count);
            }
        }

        // Solo tras una consulta exitosa, también los que no aparecen: el selector del tablero no repite la consulta.
        foreach (var nombre in pendientes)
        {
            var regional = encontrados.GetValueOrDefault(nombre);
            cache.GuardarUsuario(nombre, regional);
            if (regional is not null)
            {
                resultado[nombre] = regional;
            }
        }

        return resultado;
    }

    public async Task<IReadOnlyList<string>> ListarNombresUsuarioAsync(string regional, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regional);
        var buscada = regional.Trim();
        if (cache.IntentarRegional(buscada, out var enCache))
        {
            return enCache;
        }

        await using var conexion = await AbrirAsync(cancellationToken);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT DISTINCT {SinDominio(opciones.Value.ColumnaUsuario)} AS usuario
            FROM {Tabla()}
            WHERE UPPER({Texto(opciones.Value.ColumnaRegional)}) = @regional
              AND {SinDominio(opciones.Value.ColumnaUsuario)} <> N'';
            """;
        comando.Parameters.Add(new SqlParameter("@regional", System.Data.SqlDbType.NVarChar, 255) { Value = buscada.ToUpperInvariant() });
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

        cache.GuardarRegional(buscada, nombres);
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

    /// <summary>
    /// Columna de usuario como texto recortado y sin el prefijo de dominio: <c>boterosoto\apérez</c> → <c>apérez</c> (#81). Solo se
    /// quita hasta la primera <c>\</c>; un valor sin <c>\</c> queda igual. Se hace en SQL para que el filtro siga parametrizado y
    /// la base devuelva solo las filas de los usuarios pedidos (la tabla es pequeña: el índice de la columna no hace falta).
    /// </summary>
    private static string SinDominio(string columna)
    {
        var texto = Texto(columna);
        return $"LTRIM(RTRIM(CASE WHEN CHARINDEX(N'\\', {texto}) > 0 THEN SUBSTRING({texto}, CHARINDEX(N'\\', {texto}) + 1, 255) ELSE {texto} END))";
    }
}
