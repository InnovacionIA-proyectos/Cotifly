using System.Data;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// NIT por BP para la sincronización del margen de SAP TM (HU-042.2, #243): <c>dbo.DimBPSolicitante</c> (columnas <c>BP</c> y <c>NIT</c>) de
/// <c>DWCubosOperacionesEBS</c>, por <c>ConnectionStrings:DwOperaciones</c> (usuario de solo lectura). Solo <c>SELECT</c> parametrizado, en
/// lotes de <see cref="TamanoLote"/> BP (SQL Server admite 2100 parámetros), como el script de Datos y
/// <c>clsDirectorioEjecutivosDimEjecutivo</c>.
/// </summary>
/// <remarks>
/// El BP se busca tal como lo manda SAP (igual que el script de Datos). Las columnas se leen como texto (<c>CAST ... AS nvarchar</c>, por si
/// el cubo las tiene numéricas) y recortadas. Un NIT vacío o de más de 20 caracteres (el largo de <c>SapMargenCliente.nit</c>) se descarta: la
/// fila queda sin NIT y se anota un warning. Un BP con varios NIT se queda con el último leído, como el script. Los fallos de la base se
/// propagan: la corrida queda en <c>error</c> y la tabla no se toca.
/// </remarks>
public sealed class clsDirectorioNitDwOperaciones(IOptions<clsOpcionesConexiones> conexiones, ILogger<clsDirectorioNitDwOperaciones> logger)
{
    /// <summary>BP por consulta.</summary>
    public const int TamanoLote = 500;

    /// <summary>Largo de <c>SapMargenCliente.nit</c> (script 17).</summary>
    public const int LargoNit = 20;

    /// <summary>Límite de cada consulta al DW, en segundos.</summary>
    public const int TiempoEsperaSegundos = 60;

    /// <summary>Hay <c>ConnectionStrings:DwOperaciones</c>.</summary>
    public bool Configurado => !string.IsNullOrWhiteSpace(conexiones.Value.DwOperaciones);

    /// <summary>NIT de cada BP encontrado (clave sin distinguir mayúsculas). Solo con <see cref="Configurado"/>.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ObtenerNitsAsync(IEnumerable<string> bps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bps);
        if (!Configurado)
        {
            throw new InvalidOperationException("ConnectionStrings:DwOperaciones no está configurada.");
        }

        var nits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descartados = 0;
        var pendientes = bps.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pendientes.Count == 0)
        {
            return nits;
        }

        await using var conexion = new SqlConnection(conexiones.Value.DwOperaciones);
        await conexion.OpenAsync(cancellationToken);
        foreach (var lote in pendientes.Chunk(TamanoLote))
        {
            await using var comando = conexion.CreateCommand();
            var parametros = new List<string>(lote.Length);
            for (var i = 0; i < lote.Length; i++)
            {
                var nombre = $"@bp{i}";
                parametros.Add(nombre);
                comando.Parameters.Add(new SqlParameter(nombre, SqlDbType.NVarChar, 20) { Value = lote[i] });
            }

            comando.CommandText = $"""
                SELECT LTRIM(RTRIM(CAST(BP AS NVARCHAR(40)))) AS bp, LTRIM(RTRIM(CAST(NIT AS NVARCHAR(60)))) AS nit
                FROM dbo.DimBPSolicitante
                WHERE BP IN ({string.Join(", ", parametros)});
                """;
            comando.CommandTimeout = TiempoEsperaSegundos;
            await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
            while (await lector.ReadAsync(cancellationToken))
            {
                if (lector.IsDBNull(0) || lector.IsDBNull(1))
                {
                    continue;
                }

                var nit = lector.GetString(1);
                if (nit.Length is 0 or > LargoNit)
                {
                    descartados++;
                    continue;
                }

                nits[lector.GetString(0)] = nit;
            }
        }

        if (descartados > 0)
        {
            logger.LogWarning("DimBPSolicitante trajo {Descartados} NIT vacíos o de más de {Largo} caracteres: esas filas quedan sin NIT.", descartados, LargoNit);
        }

        return nits;
    }
}
