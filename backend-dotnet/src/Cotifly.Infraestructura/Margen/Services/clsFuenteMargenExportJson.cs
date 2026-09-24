using System.Text.Json;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Fuente SAP TM <c>export_json</c> (port de <c>ExportJsonFuente</c>, <c>mcp_server/margen/export_fuente.py</c>): lee el export
/// de <c>RentabClienteSet</c> de <c>SapTm:ExportPath</c> y lo indexa por (SalesOrgId, TrqType, OrderPartyId) para búsquedas O(1).
/// Devuelve el <c>Value</c> crudo (porcentaje); el resolver lo divide por 100 una sola vez.
/// </summary>
/// <remarks>
/// <para><b>Caché.</b> El índice vive en memoria (singleton) y se recarga cuando cambia la fecha de modificación o el tamaño
/// del archivo, que se revisan en cada consulta (una llamada al sistema de archivos, microsegundos frente al resto de la
/// cotización). Se eligió sobre un TTL porque un export reemplazado se ve en la siguiente consulta, sin ventana de datos
/// viejos ni lecturas periódicas inútiles, y sobre <c>FileSystemWatcher</c> porque sus eventos no son confiables en
/// volúmenes montados de Docker. El Python lo cargaba una sola vez por proceso y exigía reiniciar para ver un export nuevo.</para>
/// <para><b>Errores.</b> Archivo ausente, JSON inválido o una fila sin los campos esperados →
/// <see cref="clsFuenteMargenNoDisponibleException"/> (el resolver cae a la política con el motivo "SAP TM no disponible") y la caché
/// se descarta: no se sirve un export viejo cuando el vigente está mal, y en cuanto el archivo vuelve a estar bien se recupera
/// sin reiniciar. Para reemplazar el export sin que una consulta lea un archivo a medio escribir, copiarlo con otro nombre y
/// renombrarlo (operación atómica en el mismo volumen). El mensaje no incluye la ruta (va al log), a diferencia del Python.</para>
/// </remarks>
public sealed class clsFuenteMargenExportJson(IOptions<clsOpcionesSapTm> opciones, ILogger<clsFuenteMargenExportJson> logger) : IClsFuenteMargen
{
    private readonly string? _ruta = string.IsNullOrWhiteSpace(opciones.Value.ExportPath) ? null : opciones.Value.ExportPath.Trim();
    private readonly SemaphoreSlim _candado = new(1, 1);
    private volatile clsInstantanea? _instantanea;

    public string Modo => clsOpcionesSapTm.ModoExportJson;

    public bool EstaConfigurada => _ruta is not null;

    public async Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default)
    {
        var indice = await IndiceAsync(cancellationToken);
        return indice.TryGetValue((salesOrg, trqType, bpCliente), out var valor) ? valor : null;
    }

    private async Task<IReadOnlyDictionary<(string, string, string), decimal>> IndiceAsync(CancellationToken cancellationToken)
    {
        if (_ruta is null)
        {
            throw new clsFuenteMargenNoDisponibleException("La fuente export_json de SAP TM no tiene archivo configurado (SapTm:ExportPath).");
        }

        var firma = Firma();
        if (_instantanea is { } vigente && vigente.Firma == firma)
        {
            return vigente.Indice;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            firma = Firma();
            if (_instantanea is { } recargada && recargada.Firma == firma)
            {
                return recargada.Indice;
            }

            var indice = await CargarAsync(cancellationToken);
            _instantanea = new clsInstantanea(firma, indice);
            logger.LogInformation("Export de SAP TM cargado: {Filas} filas de {Ruta}.", indice.Count, _ruta);
            return indice;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>Fecha de modificación y tamaño actuales; si el archivo no existe descarta la caché y lanza.</summary>
    private (DateTime ModificadoUtc, long Largo) Firma()
    {
        var archivo = new FileInfo(_ruta!);
        if (!archivo.Exists)
        {
            _instantanea = null;
            logger.LogWarning("No existe el export de SAP TM en {Ruta}.", _ruta);
            throw new clsFuenteMargenNoDisponibleException("No existe el export de SAP TM (SapTm:ExportPath).");
        }

        return (archivo.LastWriteTimeUtc, archivo.Length);
    }

    private async Task<Dictionary<(string, string, string), decimal>> CargarAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var flujo = new FileStream(
                _ruta!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 16_384, useAsync: true);
            using var documento = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);

            var indice = new Dictionary<(string, string, string), decimal>();
            foreach (var fila in clsFormatoRentabSap.Resultados(documento.RootElement))
            {
                var llave = (
                    clsFormatoRentabSap.Texto(fila, clsFormatoRentabSap.CampoSalesOrg),
                    clsFormatoRentabSap.Texto(fila, clsFormatoRentabSap.CampoTrqType),
                    clsFormatoRentabSap.Texto(fila, clsFormatoRentabSap.CampoOrderParty));
                indice[llave] = clsFormatoRentabSap.Valor(fila); // filas repetidas: gana la última, como el dict del Python
            }

            return indice;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            _instantanea = null;
            logger.LogWarning("Export de SAP TM ilegible en {Ruta}: {Motivo}.", _ruta, Motivo(ex));
            logger.LogDebug(ex, "Detalle del export de SAP TM ilegible en {Ruta}.", _ruta);
            throw new clsFuenteMargenNoDisponibleException(
                ex is FileNotFoundException or DirectoryNotFoundException
                    ? "No existe el export de SAP TM (SapTm:ExportPath)."
                    : $"Export SAP ilegible: {Motivo(ex)}",
                ex);
        }
    }

    private static string Motivo(Exception ex) => ex switch
    {
        FormatException => ex.Message,
        JsonException => "JSON inválido",
        _ => "no se pudo leer el archivo",
    };

    private sealed record clsInstantanea((DateTime ModificadoUtc, long Largo) Firma, IReadOnlyDictionary<(string, string, string), decimal> Indice);
}
