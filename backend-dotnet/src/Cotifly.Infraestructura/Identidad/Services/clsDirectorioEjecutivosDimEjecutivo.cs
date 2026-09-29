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
/// se leen como texto (<c>CAST ... AS nvarchar</c>), sin espacios, tabuladores ni saltos de línea a los lados y en mayúsculas para
/// comparar. La columna de usuario (<c>USUARIO</c>, <c>boterosoto\usuario</c>) se parte en la ÚLTIMA <c>\</c>: el nombre se
/// compara en SQL y el dominio se valida aquí contra <see cref="clsOpcionesRegional.Dominio"/> (#81); una fila de otro dominio no
/// cuenta. Los fallos de la base se propagan (el tablero responde 503; el login sigue sin refrescar) y no se guardan; las
/// respuestas exitosas se reutilizan 60 s (<see cref="clsCacheDirectorioEjecutivos"/>).
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

        var filas = new List<(string Usuario, string Dominio, string? Regional)>();
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

                var columna = opciones.Value.ColumnaUsuario;
                comando.CommandText = $"""
                    SELECT {SinDominio(columna)} AS usuario, {SoloDominio(columna)} AS dominio, {Texto(opciones.Value.ColumnaRegional)} AS regional
                    FROM {Tabla()}
                    WHERE UPPER({SinDominio(columna)}) IN ({string.Join(", ", parametros)});
                    """;
                comando.CommandTimeout = opciones.Value.TiempoEsperaSegundos;
                await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
                while (await lector.ReadAsync(cancellationToken))
                {
                    filas.Add((lector.GetString(0), lector.IsDBNull(1) ? string.Empty : lector.GetString(1), lector.IsDBNull(2) ? null : lector.GetString(2)));
                }
            }
        }

        var encontrados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grupo in DelDominio(filas).GroupBy(f => f.Usuario, StringComparer.OrdinalIgnoreCase))
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
        var columna = opciones.Value.ColumnaUsuario;
        comando.CommandText = $"""
            SELECT DISTINCT {SinDominio(columna)} AS usuario, {SoloDominio(columna)} AS dominio
            FROM {Tabla()}
            WHERE UPPER({Texto(opciones.Value.ColumnaRegional)}) = @regional
              AND {SinDominio(columna)} <> N'';
            """;
        comando.Parameters.Add(new SqlParameter("@regional", System.Data.SqlDbType.NVarChar, 255) { Value = buscada.ToUpperInvariant() });
        comando.CommandTimeout = opciones.Value.TiempoEsperaSegundos;

        var filas = new List<(string Usuario, string Dominio, string? Regional)>();
        await using (var lector = await comando.ExecuteReaderAsync(cancellationToken))
        {
            while (await lector.ReadAsync(cancellationToken))
            {
                if (!lector.IsDBNull(0) && lector.GetString(0) is { Length: > 0 } nombre)
                {
                    filas.Add((nombre, lector.IsDBNull(1) ? string.Empty : lector.GetString(1), buscada));
                }
            }
        }

        IReadOnlyList<string> nombres = [.. DelDominio(filas).Select(f => f.Usuario).Distinct(StringComparer.OrdinalIgnoreCase)];
        cache.GuardarRegional(buscada, nombres);
        return nombres;
    }

    /// <summary>
    /// Filas cuyo dominio es el configurado (<see cref="clsOpcionesRegional.Dominio"/>, sin distinguir mayúsculas) o no tienen
    /// dominio (valor sin <c>\</c>). Con <c>Regional:Dominio</c> vacío se acepta cualquiera. Una fila de otro dominio
    /// (<c>otrodominio\jperez</c>) no cuenta y deja un warning solo con el nombre de usuario.
    /// </summary>
    private IEnumerable<(string Usuario, string Dominio, string? Regional)> DelDominio(IEnumerable<(string Usuario, string Dominio, string? Regional)> filas)
    {
        var esperado = opciones.Value.Dominio?.Trim() ?? string.Empty;
        foreach (var fila in filas)
        {
            if (esperado.Length == 0 || fila.Dominio.Length == 0 || string.Equals(fila.Dominio, esperado, StringComparison.OrdinalIgnoreCase))
            {
                yield return fila;
            }
            else
            {
                logger.LogWarning("La tabla de ejecutivos tiene al usuario '{Usuario}' con un dominio distinto del configurado: esa fila se ignora.", fila.Usuario);
            }
        }
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

    /// <summary>
    /// Texto sin espacios, tabuladores ni saltos de línea (<c>CHAR(9)</c>, <c>CHAR(10)</c>, <c>CHAR(13)</c>) a los lados. Se usa
    /// <c>REPLACE</c> + <c>LTRIM</c>/<c>RTRIM</c> en vez de <c>TRIM(... FROM ...)</c> porque no se conoce la versión ni el nivel de
    /// compatibilidad de <c>DWCubosEBSSAP</c> (<c>TRIM</c> exige SQL Server 2017). Un nombre de usuario no lleva esos caracteres dentro.
    /// </summary>
    private static string Limpio(string expresion) =>
        $"LTRIM(RTRIM(REPLACE(REPLACE(REPLACE({expresion}, CHAR(9), N' '), CHAR(10), N' '), CHAR(13), N' ')))";

    /// <summary>Columna leída como texto limpio.</summary>
    private static string Texto(string columna) => Limpio($"CAST([{columna}] AS nvarchar(255))");

    /// <summary>Posición de la ÚLTIMA <c>\</c> contada desde el final (0 si no hay).</summary>
    private static string PosicionDesdeElFinal(string texto) => $"CHARINDEX(N'\\', REVERSE({texto}))";

    /// <summary>
    /// Nombre de usuario: lo que va después de la ÚLTIMA <c>\</c>, limpio (<c>boterosoto\apérez</c> → <c>apérez</c>,
    /// <c>a\\b</c> → <c>b</c>); sin <c>\</c>, el valor tal cual. En SQL para que el filtro siga parametrizado (la tabla es pequeña:
    /// el índice de la columna no hace falta).
    /// </summary>
    private static string SinDominio(string columna)
    {
        var texto = Texto(columna);
        var posicion = PosicionDesdeElFinal(texto);
        return Limpio($"CASE WHEN {posicion} > 0 THEN RIGHT({texto}, {posicion} - 1) ELSE {texto} END");
    }

    /// <summary>
    /// Dominio: lo que va antes de la última <c>\</c>, sin las <c>\</c> sobrantes a la derecha (<c>boterosoto\\x</c> → <c>boterosoto</c>,
    /// <c>a\\b</c> → <c>a</c>); vacío si no hay <c>\</c>.
    /// </summary>
    private static string SoloDominio(string columna)
    {
        var texto = Texto(columna);
        var posicion = PosicionDesdeElFinal(texto);
        var antes = $"LEFT({texto}, LEN({texto}) - {posicion})";
        var sinBarras = $"REVERSE(SUBSTRING(REVERSE({antes}), PATINDEX(N'%[^\\]%', REVERSE({antes}) + N'x'), 255))";
        return $"CASE WHEN {posicion} > 0 THEN {Limpio(sinBarras)} ELSE N'' END";
    }
}
