using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Implementación de <see cref="IClsGenerarPdfService"/> (port de la herramienta <c>generar_pdf</c> de
/// <c>mcp_server/server.py</c>). Orden:
/// <list type="number">
/// <item>Folio de la entrada (lo genera el servidor, ADR-011; <c>referencia</c> del payload no se usa) y payload válidos (<c>PDF_PAYLOAD_INVALIDO</c>).</item>
/// <item>Comercial desde <c>UsuariosWeb</c>: nombre, correo y celular de la firma (nunca del LLM; sin teléfono no hay "Cel").</item>
/// <item>Línea contra <c>MargenesPolitica</c> sin tildes ni mayúsculas (<c>MARGEN_LINEA_DESCONOCIDA</c> antes de renderizar, issue #44).</item>
/// <item>Folio libre (<c>FOLIO_DUPLICADO</c>): el folio lo genera #12 (ADR-011); aquí no se versiona como en el Python.</item>
/// <item>Cotización válida en el dominio (márgenes, TRM, moneda) antes de renderizar, para no dejar PDFs sin fila.</item>
/// <item>Coherencia entre lo impreso y la trazabilidad (ADR-003, ADR-004, HU-012.3): cada fila y servicio en la moneda de la
/// cotización; con <c>PrecioFinal</c>, costo/margen deben dar ese precio y la tarifa de cada fila debe ser ese precio.</item>
/// <item>Render (<c>PDF_RENDER_NO_DISPONIBLE</c> si falla), guardado sin reemplazar un archivo existente y registro.
/// Si la base rechaza la fila, el PDF recién guardado se borra (compensación).</item>
/// </list>
/// </summary>
public sealed partial class clsGenerarPdfService(
    IClsUsuarioWebRepository usuarios,
    IClsCotizacionRepository cotizaciones,
    IClsMargenResolverService margen,
    IClsGeneradorPdf generador,
    IClsAlmacenPdf almacen,
    IClsUnitOfWork unidadTrabajo,
    IClsCalendarioNegocio calendario,
    IOptions<clsOpcionesPdf> opciones,
    ILogger<clsGenerarPdfService> logger) : IClsGenerarPdfService
{
    public async Task<clsResultado<clsSalidaPdf>> GenerarAsync(clsEntradaGenerarPdf entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (entrada.UsuarioId == Guid.Empty)
        {
            throw new ArgumentException("generar_pdf exige el comercial autenticado (usuarioId).", nameof(entrada));
        }

        var payload = entrada.Cotizacion;
        var folio = clsTextoSeguro.LimpiarLinea(entrada.Folio);
        var errorFolio = ValidarFolio(folio);
        if (errorFolio is not null)
        {
            return errorFolio;
        }

        var errorPayload = clsValidadorCotizacionPdfPayload.Validar(payload);
        if (errorPayload is not null)
        {
            return errorPayload;
        }

        var usuario = await usuarios.ObtenerPorIdAsync(entrada.UsuarioId, cancellationToken);
        if (usuario is null)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "No se encontró el comercial autenticado en UsuariosWeb.");
        }

        var politica = await margen.ResolverLineaAsync(payload!.LineaNegocio, cancellationToken);
        if (politica is null)
        {
            return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, $"Sin mapeo ni política para la línea '{clsTextoSeguro.LimpiarLinea(payload.LineaNegocio)}'.");
        }

        if (!politica.Activa)
        {
            return clsMargenResolverService.LineaInactiva(politica.LineaNegocio);
        }

        if (await cotizaciones.ExisteFolioAsync(folio!, cancellationToken))
        {
            return FolioDuplicado(folio!);
        }

        var traza = entrada.Trazabilidad ?? new clsTrazabilidadCotizacion();
        var metodo = (clsTextoSeguro.LimpiarLinea(traza.MetodoCalculo)
                      ?? clsTextoSeguro.LimpiarLinea(payload.MetodoCalculo)
                      ?? clsTextoSeguro.LimpiarLinea(politica.MetodoCalculo)
                      ?? clsReglaMargen.MetodoMargen).ToLowerInvariant();
        var moneda = (clsTextoSeguro.LimpiarLinea(traza.Moneda) ?? clsCotizacion.MonedaPesos).ToUpperInvariant();

        var cotizacion = CrearCotizacion(entrada, traza, folio!, politica, metodo, moneda);
        if (cotizacion.EsFallo)
        {
            return cotizacion.Error;
        }

        var incoherencia = ValidarCoherencia(payload, traza, metodo, moneda);
        if (incoherencia is not null)
        {
            return incoherencia;
        }

        var firma = new clsFirmaDocumento(
            usuario.NombreCompleto ?? usuario.NombreUsuario ?? string.Empty,
            clsTextoSeguro.LimpiarLinea(payload.Comercial?.Cargo),
            usuario.Email,
            clsTextoSeguro.LimpiarLinea(usuario.Telefono));
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, folio!, firma, calendario.Hoy, moneda, traza.TrmAplicada, PisoEnPesos(traza, metodo, moneda));
        if (documento.EsFallo)
        {
            return documento.Error;
        }

        clsPdfGenerado pdf;
        try
        {
            pdf = generador.Generar(documento.Valor);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo renderizar el PDF del folio {Folio}.", folio);
            return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "No se pudo generar el PDF de la cotización. Reintenta más tarde o contacta a soporte.");
        }

        var nombre = folio + ".pdf";
        var pdfPath = await almacen.GuardarNuevoAsync(nombre, pdf.Contenido, cancellationToken);
        if (pdfPath is null)
        {
            // El archivo existe sin fila en la base (otra generación en curso con el mismo folio, o una que se cayó
            // entre guardar y registrar): nunca se reemplaza.
            return FolioDuplicado(folio!, $"ya existe el archivo {nombre} en el almacén");
        }

        cotizacion.Valor.AsignarPdf(pdfPath);
        cotizaciones.Agregar(cotizacion.Valor);
        try
        {
            await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        }
        catch (clsViolacionUnicidadException ex)
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            return ex.Error.Codigo == clsCodigosError.FolioDuplicado ? FolioDuplicado(folio!) : ex.Error;
        }
        catch
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            throw;
        }

        logger.LogInformation("PDF {Folio} generado ({Paginas} páginas, {Bytes} bytes).", folio, pdf.Paginas, pdf.Contenido.Length);
        return new clsSalidaPdf(cotizacion.Valor.Id, folio!, UrlPdf(nombre), RegistradaEnBd: true);
    }

    /// <summary>El folio es obligatorio, de máximo 50 caracteres y solo letras, números, guion y guion bajo (nombre del archivo).</summary>
    public static clsErrorDominio? ValidarFolio(string? folio)
    {
        if (folio is null)
        {
            return new clsErrorDominio(
                clsCodigosError.PdfPayloadInvalido,
                "Falta el folio de la cotización: lo genera el servidor (ADR-011) y llega en la entrada; la 'referencia' del payload no se usa.");
        }

        if (folio.Length > clsCotizacion.LargoMaximoFolio || !ExpresionFolio().IsMatch(folio))
        {
            return new clsErrorDominio(
                clsCodigosError.PdfPayloadInvalido,
                $"El folio '{folio}' no es válido: máximo {clsCotizacion.LargoMaximoFolio} caracteres entre letras, números, '-' y '_'.");
        }

        return null;
    }

    /// <summary>
    /// Lo impreso debe cuadrar con la trazabilidad del estado (ADR-003: el LLM no pone números; ADR-004: el filtro de #12
    /// sobrescribe costo, margen y tarifa de las filas y los precios de los servicios desde el estado):
    /// <list type="bullet">
    /// <item>Cada fila y cada servicio conexo con moneda explícita debe estar en la moneda de la cotización (HU-012.3).</item>
    /// <item>Con <c>PrecioCop</c> y costo + margen aplicado, <c>PrecioCop</c> debe ser el precio en pesos calculado (es el piso del PDF en USD).</item>
    /// <item>Con <c>PrecioFinal</c> y costo + margen aplicado, el precio calculado con el método debe ser <c>PrecioFinal</c> (en USD, el precio en
    /// pesos convertido con la TRM de la trazabilidad, HU-012.2).</item>
    /// <item>Con <c>PrecioFinal</c>, la tarifa impresa de cada fila (dada o calculada) debe ser <c>PrecioFinal</c>. La tabla
    /// Cotizaciones guarda un solo precio: una cotización con varias tarifas distintas no puede traer <c>PrecioFinal</c>
    /// (pendiente de modelo si negocio necesita guardar un precio por fila).</item>
    /// </list>
    /// </summary>
    public static clsErrorDominio? ValidarCoherencia(clsCotizacionPdfPayload payload, clsTrazabilidadCotizacion traza, string metodo, string moneda)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(traza);
        var filas = clsValidadorCotizacionPdfPayload.SeccionesDe(payload).SelectMany(s => s.Filas ?? []).Where(f => f is not null).ToList();

        foreach (var fila in filas)
        {
            if (clsTextoSeguro.LimpiarLinea(fila.Moneda) is { } monedaFila && !string.Equals(monedaFila, moneda, StringComparison.OrdinalIgnoreCase))
            {
                return Invalido($"La fila '{clsTextoSeguro.LimpiarLinea(fila.Destino)}' está en {monedaFila.ToUpperInvariant()} y la cotización en {moneda}: todas las tarifas van en la moneda de la cotización (HU-012.3).");
            }
        }

        foreach (var servicio in payload.ServiciosConexos ?? [])
        {
            if (clsTextoSeguro.LimpiarLinea(servicio?.Moneda) is { } monedaServicio && !string.Equals(monedaServicio, moneda, StringComparison.OrdinalIgnoreCase))
            {
                return Invalido($"El servicio conexo '{clsTextoSeguro.LimpiarLinea(servicio!.Descripcion)}' está en {monedaServicio.ToUpperInvariant()} y la cotización en {moneda} (HU-012.3).");
            }
        }

        if (traza.PrecioFinal is not { } precioFinal)
        {
            return null;
        }

        if (traza.PrecioCop is { } precioCop && traza.Costo is { } costoPiso && traza.MargenAplicado is { } margenPiso
            && clsCalculadoraPrecio.Calcular(costoPiso, margenPiso, metodo) is { EsExitoso: true } enPesos && enPesos.Valor.Precio != precioCop)
        {
            return Invalido(
                $"El valor en pesos de la trazabilidad ({clsFormatoMoneda.Formatear(precioCop, "COP")}) no corresponde al costo y margen aplicado con el método '{metodo}' "
                + $"({clsFormatoMoneda.Formatear(enPesos.Valor.Precio, "COP")}): es el mínimo a pagar que imprime el PDF en USD (HU-012.3).");
        }

        if (traza.Costo is { } costo && traza.MargenAplicado is { } margenAplicado)
        {
            // El costo está en COP; en otra moneda el precio esperado es el de pesos convertido con la TRM de la trazabilidad (HU-012.2).
            var calculado = clsCalculadoraPrecio.Calcular(costo, margenAplicado, metodo)
                .Coincidir(
                    p => clsConversorMoneda.EsPesos(moneda) ? p.Precio : clsConversorMoneda.PesosADolares(p.Precio, traza.TrmAplicada),
                    e => (clsResultado<decimal>)e);
            if (calculado.EsFallo || calculado.Valor != precioFinal)
            {
                var obtenido = calculado.EsExitoso ? clsFormatoMoneda.Formatear(calculado.Valor, moneda) : calculado.Error.Mensaje;
                return Invalido($"El precio final de la trazabilidad ({clsFormatoMoneda.Formatear(precioFinal, moneda)}) no corresponde al costo y margen aplicado con el método '{metodo}' ({obtenido}).");
            }
        }

        var metodoFilas = clsTextoSeguro.LimpiarLinea(payload.MetodoCalculo)?.ToLowerInvariant() ?? clsReglaMargen.MetodoMargen;
        foreach (var fila in filas)
        {
            if (clsConstructorDocumentoCotizacion.CalcularTarifa(fila, metodoFilas, out var tarifa) is { } error)
            {
                return error;
            }

            if (tarifa is { } impresa && clsConversorMoneda.Redondear(impresa, moneda) != clsConversorMoneda.Redondear(precioFinal, moneda))
            {
                return Invalido(
                    $"La tarifa impresa de la fila '{clsTextoSeguro.LimpiarLinea(fila.Destino)}' ({clsFormatoMoneda.Formatear(impresa, moneda)}) no coincide con el precio final de la cotización "
                    + $"({clsFormatoMoneda.Formatear(precioFinal, moneda)}): costo, margen y tarifa se toman del estado de la conversación, no del payload (ADR-004).");
            }
        }

        return null;
    }

    /// <summary>
    /// Piso de una cotización en USD (HU-012.3, ADR-014): el valor cotizado en pesos de la trazabilidad (<c>PrecioCop</c>, ya verificado
    /// contra costo y margen) o, si no viene, el precio en pesos recalculado con el costo y el margen aplicado persistidos. En COP, nulo.
    /// </summary>
    private static decimal? PisoEnPesos(clsTrazabilidadCotizacion traza, string metodo, string moneda)
    {
        if (clsConversorMoneda.EsPesos(moneda))
        {
            return null;
        }

        if (traza.PrecioCop is { } precioCop)
        {
            return precioCop;
        }

        return traza.Costo is { } costo && traza.MargenAplicado is { } margen && clsCalculadoraPrecio.Calcular(costo, margen, metodo) is { EsExitoso: true } calculado
            ? calculado.Valor.Precio
            : null;
    }

    private static clsResultado<clsCotizacion> CrearCotizacion(
        clsEntradaGenerarPdf entrada, clsTrazabilidadCotizacion traza, string folio, clsMargenPolitica politica, string metodo, string moneda)
    {
        OrigenMargen? origenMargen = null;
        if (clsTextoSeguro.LimpiarLinea(traza.OrigenMargen) is { } codigoOrigen)
        {
            if (!clsCodigoEnum.TryDesdeCodigo<OrigenMargen>(codigoOrigen, out var origen))
            {
                return Invalido($"'origen_margen' debe ser sap_tm, politica_linea o negociado (llegó '{codigoOrigen}').");
            }

            origenMargen = origen;
        }

        OrigenCliente? origenCliente = null;
        if (clsTextoSeguro.LimpiarLinea(traza.OrigenCliente) is { } codigoCliente)
        {
            if (!clsCodigoEnum.TryDesdeCodigo<OrigenCliente>(codigoCliente, out var origen))
            {
                return Invalido($"'origen_cliente' debe ser cliente o prospecto (llegó '{codigoCliente}').");
            }

            origenCliente = origen;
        }

        var creada = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            Version = entrada.Version,
            LineaNegocioId = politica.Id,
            ClienteNombre = clsTextoSeguro.LimpiarLinea(entrada.Cotizacion.Cliente),
            NitCliente = clsTextoSeguro.LimpiarLinea(traza.NitCliente),
            OrigenCliente = origenCliente,
            CosteoId = clsTextoSeguro.LimpiarLinea(traza.CosteoId),
            Costo = traza.Costo,
            MargenPolitica = traza.MargenPolitica,
            MargenAplicado = traza.MargenAplicado,
            OrigenMargen = origenMargen,
            MetodoCalculo = metodo,
            PrecioFinal = traza.PrecioFinal,
            Moneda = moneda,
            TrmAplicada = traza.TrmAplicada,
            UsuarioId = entrada.UsuarioId,
            SpaceId = clsTextoSeguro.LimpiarLinea(entrada.SpaceId),
            ThreadId = clsTextoSeguro.LimpiarLinea(entrada.ThreadId),
        });

        return creada.EsExitoso ? creada : Invalido(creada.Error.Mensaje);
    }

    private string UrlPdf(string nombre) => clsUrlPdf.Construir(opciones.Value.BaseUrl, nombre);

    private static clsErrorDominio FolioDuplicado(string folio, string? detalle = null) =>
        new(clsCodigosError.FolioDuplicado, $"Ya existe una cotización con el folio {folio}.", detalle);

    private static clsErrorDominio Invalido(string mensaje) => new(clsCodigosError.PdfPayloadInvalido, mensaje);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionFolio();
}
