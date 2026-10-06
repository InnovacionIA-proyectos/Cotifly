using System.Diagnostics;
using System.Globalization;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Servicios conexos del catálogo de Nexo (HU-013.3, ADR-018 §4): <c>GET /web/servicios-conexos</c> (el buscador, sin costos) y
/// <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos</c> (el botón "Agregar conexo" y el Confirmar de la acción propuesta
/// <c>agregar_servicios_conexos</c>). Nexo es la fuente de verdad del costo: el conexo se agrega en Nexo y Cotifly solo le aplica el
/// margen de la cotización.
/// </summary>
public interface IClsServiciosConexosCosteoService
{
    /// <summary>
    /// Página del catálogo de Nexo con los servicios activos, sin <c>costoUnitario</c>. <c>SOLICITUD_INVALIDA</c> (400) con un filtro
    /// fuera de los límites del contrato; <c>NEXO_NO_DISPONIBLE</c> (503) y <c>NEXO_SIN_PERMISO</c> (403) de Nexo.
    /// </summary>
    Task<clsResultado<clsPaginaServiciosConexos>> BuscarAsync(
        Guid usuarioId, string? q, string? categoria, int? page, int? pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Agrega los conexos en Nexo, relee la cotización de Nexo y les aplica el margen (quedan incluidos). Errores en este orden:
    /// <c>SOLICITUD_INVALIDA</c> (400, forma de <paramref name="items"/>), <c>COTIZACION_NO_ENCONTRADA</c> (404, también ajena),
    /// <c>CONFLICTO_CONCURRENCIA</c> (409), <c>ESTADO_NO_PERMITE</c> (409, licitación), <c>COSTEO_NO_LISTO</c> y <c>PDF_YA_ENVIADO</c>
    /// (409), <c>CONEXO_INVALIDO</c> (400, línea inexistente), <c>LINEA_SIN_COTIZACION_NEXO</c> (409) y los de Nexo. Si Nexo aceptó
    /// pero la relectura falla, <c>NEXO_NO_DISPONIBLE</c> con <c>agregado_en_nexo = true</c> (el polling lo recoge).
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AgregarAsync(
        Guid usuarioId, string folio, IReadOnlyList<clsConexoAgregarEntrada>? items, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsServiciosConexosCosteoService"/>.
/// <list type="bullet">
/// <item><b>Línea de Nexo.</b> <c>linea_orden</c> → la línea de Cotifly (<c>CotizacionLineasSolicitud.orden</c>) → su
/// <c>lineaSolicitudNexoId</c> (guardado por el polling) → la línea de la cotización de Nexo con ese <c>lineaSolicitudId</c>, cuyo id
/// (<see cref="clsLineaCosteoNexo.Id"/>) se lee de Nexo justo antes de agregar (<c>GET /api/cotizaciones/{id}</c>). Así el
/// <c>lineaId</c> que viaja siempre es el vigente en Nexo y no hace falta una columna nueva: si Nexo recrea las líneas de su
/// cotización, el id guardado quedaría viejo.</item>
/// <item><b>Precio.</b> Cada conexo de la cotización de Nexo pasa por <see cref="clsPrecioMonedaCosteo"/> (la función común del
/// polling y del ajuste: margen y método de la cotización, moneda y TRM fijada). Los que ya estaban conservan si el comercial los
/// incluye; los nuevos quedan incluidos. El JSON resultante es el mismo que arma el polling, así que la siguiente consulta no lo
/// cambia.</item>
/// <item><b>Concurrencia.</b> La versión se verifica antes de llamar a Nexo (un <c>If-Match</c> viejo no agrega nada) y otra vez al
/// guardar. El doble clic lo cubre el <c>Idempotency-Key</c> de la API.</item>
/// </list>
/// </summary>
public sealed class clsServiciosConexosCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsServiciosConexosNexo nexo,
    IClsPrecioService precios,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsServiciosConexosCosteoService
{
    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "agregar_servicios_conexos";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Servicios conexos después del envío";

    /// <summary>Clave de <c>detalle</c> cuando Nexo ya agregó el conexo pero Cotifly no pudo terminar (contrato).</summary>
    public const string ClaveAgregadoEnNexo = "agregado_en_nexo";

    /// <summary>Motivo (<c>detalle.motivo</c>) de <c>ESTADO_NO_PERMITE</c> en una licitación.</summary>
    public const string MotivoLicitacion = "LICITACION";

    public const int MaximoItems = 20;
    public const decimal CantidadMinima = 0.01m;
    public const decimal CantidadMaxima = 999_999.99m;
    public const int LargoMaximoBusqueda = 100;
    public const int LargoMaximoCategoria = 20;
    public const int TamanoPaginaPorDefecto = 20;
    public const int TamanoPaginaMaximo = 50;

    public async Task<clsResultado<clsPaginaServiciosConexos>> BuscarAsync(
        Guid usuarioId, string? q, string? categoria, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Sesión no válida.");
        }

        var busqueda = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var segmento = string.IsNullOrWhiteSpace(categoria) ? null : categoria.Trim();
        var pagina = page ?? 1;
        var tamano = pageSize ?? TamanoPaginaPorDefecto;
        if (busqueda is { Length: > LargoMaximoBusqueda } || segmento is { Length: > LargoMaximoCategoria } || pagina < 1
            || tamano is < 1 or > TamanoPaginaMaximo)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"Filtro inválido: q hasta {LargoMaximoBusqueda} caracteres, categoria hasta {LargoMaximoCategoria}, page desde 1 y page_size de 1 a {TamanoPaginaMaximo}.");
        }

        var resultado = await nexo.BuscarAsync(usuarioId, new clsFiltroCatalogoConexos(busqueda, segmento, pagina, tamano), cancellationToken);
        if (resultado.EsFallo)
        {
            return resultado.Error;
        }

        // Nexo ya filtra con soloActivos=true; se descarta cualquier inactivo por si acaso. Ningún campo de costo existe en el DTO.
        var items = resultado.Valor.Items.Where(s => s.Activo)
            .Select(s => new clsServicioConexoCatalogo(s.Id, s.Codigo, s.Nombre, s.Categoria, s.Unidad, s.Origen, s.Destino, s.Puerto, s.Ruta, s.TipoMaquina, s.Vigencia))
            .ToList();
        return new clsPaginaServiciosConexos(resultado.Valor.Total, pagina, tamano, items);
    }

    public async Task<clsResultado<clsCosteoCotizacion>> AgregarAsync(
        Guid usuarioId, string folio, IReadOnlyList<clsConexoAgregarEntrada>? items, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var validados = Validar(items);
        if (validados.EsFallo)
        {
            return validados.Error;
        }

        var cronometro = Stopwatch.StartNew();
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var (resultado, agregados, pdfInvalidado) = await AgregarAsync(cotizacion, usuarioId, validados.Valor, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new
            {
                folio = cotizacion.Folio,
                items = validados.Valor.Select(i => new { linea_orden = i.LineaOrden, servicio_id = i.ServicioId, cantidad = i.Cantidad }).ToList(),
            },
            resultado.EsExitoso
                ? (object)new { conexos_agregados = agregados, pdf_invalidado = pdfInvalidado }
                : new
                {
                    error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje, detalle = resultado.Error.Detalle },
                    agregado_en_nexo = resultado.Error.Datos?.ContainsKey(ClaveAgregadoEnNexo) == true,
                    pdf_invalidado = pdfInvalidado,
                },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    /// <summary>
    /// Forma de los items (el esquema <c>AgregarServiciosConexosWeb</c>): de 1 a <see cref="MaximoItems"/>, cada uno con
    /// <c>linea_orden</c> ≥ 1, <c>servicio_id</c> UUID y <c>cantidad</c> de <see cref="CantidadMinima"/> a <see cref="CantidadMaxima"/>.
    /// Fuera de eso, <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>) sin tocar nada.
    /// </summary>
    public static clsResultado<IReadOnlyList<clsConexoValidado>> Validar(IReadOnlyList<clsConexoAgregarEntrada>? items)
    {
        if (items is null || items.Count == 0 || items.Count > MaximoItems)
        {
            return Invalido($"'items' debe traer de 1 a {MaximoItems} servicios conexos.");
        }

        var validados = new List<clsConexoValidado>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null)
            {
                return Invalido($"items[{i}] es obligatorio.");
            }

            if (item.LineaOrden is not { } orden || orden < 1)
            {
                return Invalido($"items[{i}].linea_orden es obligatorio y debe ser 1 o mayor.");
            }

            if (!Guid.TryParse(item.ServicioId, out var servicio))
            {
                return Invalido($"items[{i}].servicio_id debe ser el id (UUID) de un servicio del catálogo.");
            }

            if (item.Cantidad is not { } cantidad || cantidad < CantidadMinima || cantidad > CantidadMaxima || decimal.Round(cantidad, 2) != cantidad)
            {
                return Invalido($"items[{i}].cantidad debe estar entre {CantidadMinima.ToString(CultureInfo.InvariantCulture)} y {CantidadMaxima.ToString(CultureInfo.InvariantCulture)}, con hasta 2 decimales.");
            }

            validados.Add(new clsConexoValidado(orden, servicio.ToString("D"), cantidad));
        }

        return clsResultado<IReadOnlyList<clsConexoValidado>>.Exito(validados);
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, int Agregados, bool PdfInvalidado)> AgregarAsync(
        clsCotizacion cotizacion, Guid usuarioId, IReadOnlyList<clsConexoValidado> items, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), 0, false);
        }

        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        if (clsCosteoCotizacionService.EsPliego(cotizacion, lineas))
        {
            // ADR-017 §6 y ADR-018 §4: el PDF de pliego no imprime conexos; las licitaciones no los admiten en esta iteración.
            return (new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La cotización {cotizacion.Folio} es una licitación: no admite servicios conexos en esta iteración.", MotivoLicitacion), 0, false);
        }

        if (!cotizacion.TieneCostoDisponible || cotizacion.MargenAplicado is null || cotizacion.MetodoCalculo is null)
        {
            return (new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado: espera el costeo para agregar conexos."), 0, false);
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersion) is { } entregada)
        {
            return (entregada, 0, false);
        }

        // linea_orden → línea de Cotifly. Una cotización anterior al 0005 (sin filas de línea) solo tiene la línea 1.
        var porOrden = lineas.ToDictionary(l => l.Orden);
        if (items.FirstOrDefault(i => lineas.Count == 0 ? i.LineaOrden != 1 : !porOrden.ContainsKey(i.LineaOrden)) is { } inexistente)
        {
            return (new clsErrorDominio(clsCodigosError.ConexoInvalido,
                $"La línea {inexistente.LineaOrden} no existe en la cotización {cotizacion.Folio}.", "LINEA_INEXISTENTE"), 0, false);
        }

        if (items.FirstOrDefault(i => lineas.Count > 0 && porOrden[i.LineaOrden].LineaSolicitudNexoId is null) is { } sinNexo)
        {
            return (SinLineaNexo(cotizacion, sinNexo.LineaOrden), 0, false);
        }

        if (cotizacion.CotizacionNexoId is not { } cotizacionNexoId)
        {
            return (SinLineaNexo(cotizacion, items[0].LineaOrden), 0, false);
        }

        // El id de la línea de la cotización de Nexo se lee de Nexo justo antes de agregar (ver el resumen de la clase).
        var actual = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, cancellationToken);
        if (actual.EsFallo)
        {
            return (actual.Error, 0, false);
        }

        var paraNexo = new List<clsConexoParaNexo>();
        foreach (var item in items)
        {
            var lineaId = LineaDeNexo(actual.Valor, lineas.Count == 0 ? null : porOrden[item.LineaOrden]);
            if (lineaId is null)
            {
                return (SinLineaNexo(cotizacion, item.LineaOrden), 0, false);
            }

            paraNexo.Add(new clsConexoParaNexo(lineaId, item.ServicioId, item.Cantidad));
        }

        var agregado = await nexo.AgregarAsync(usuarioId, cotizacionNexoId, paraNexo, cancellationToken);
        if (agregado.EsFallo)
        {
            return (agregado.Error, 0, false); // nada cambia en Cotifly
        }

        // Desde aquí el conexo YA existe en Nexo: no se vuelve a llamar al POST pase lo que pase.
        var releida = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, CancellationToken.None);
        if (releida.EsFallo)
        {
            return await SinRelecturaAsync(cotizacion, usuarioId, versionEsperada, releida.Error);
        }

        var conexos = ConexosConPrecio(cotizacion, releida.Valor);
        if (conexos.EsFallo)
        {
            return (AgregadoEnNexo(conexos.Error), 0, false);
        }

        var anteriores = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Count;
        cotizacion.SeleccionarServiciosConexos(clsServiciosConexosJson.Escribir(conexos.Valor));
        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None))
        {
            // Otro cambio (el polling, otra pestaña) ganó entre la verificación y el guardado: el conexo ya está en Nexo y la
            // siguiente consulta del polling lo recoge; el comercial recarga.
            return (AgregadoEnNexo(clsReglaPdfCosteo.Conflicto(cotizacion.Folio)), 0, false);
        }

        return (clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl), Math.Max(0, conexos.Valor.Count - anteriores), pdfInvalidado);
    }

    /// <summary>
    /// Nexo agregó el conexo pero no se pudo releer la cotización: 503 <c>NEXO_NO_DISPONIBLE</c> con <c>agregado_en_nexo = true</c>.
    /// Si había PDF vigente se invalida (la cotización ya cambió en Nexo y el polling no toca los conexos de una cotización con PDF);
    /// sin PDF nada cambia en Cotifly y el polling recoge el conexo en su siguiente consulta.
    /// </summary>
    private async Task<(clsResultado<clsCosteoCotizacion>, int, bool)> SinRelecturaAsync(
        clsCotizacion cotizacion, Guid usuarioId, int versionEsperada, clsErrorDominio error)
    {
        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        if (pdfInvalidado)
        {
            cotizacion.IncrementarVersionNegocio();
            pdfInvalidado = await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None);
        }

        var noDisponible = new clsErrorDominio(clsCodigosError.NexoNoDisponible,
            "Nexo agregó el servicio conexo pero no se pudo leer de nuevo la cotización: aparecerá con su precio en la siguiente actualización. No lo vuelvas a agregar.",
            error.Detalle ?? error.Codigo);
        return (AgregadoEnNexo(noDisponible), 0, pdfInvalidado);
    }

    /// <summary>
    /// Conexos de la cotización de Nexo con su precio (<see cref="clsPrecioMonedaCosteo"/>), en el orden de Nexo y con la misma forma
    /// que el polling. Conservan <c>incluido</c> los que ya estaban; los nuevos quedan incluidos.
    /// </summary>
    public clsResultado<IReadOnlyList<clsServicioConexoCosteo>> ConexosConPrecio(clsCotizacion cotizacion, clsCotizacionNexo nexoCotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(nexoCotizacion);
        if (cotizacion.MargenAplicado is not { } margen || cotizacion.MetodoCalculo is not { } metodo)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} no tiene margen resuelto.");
        }

        var anteriores = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var conexos = new List<clsServicioConexoCosteo>();
        foreach (var conexo in nexoCotizacion.Conexos)
        {
            var precio = clsPrecioMonedaCosteo.Calcular(precios, cotizacion, conexo.ValorAplicado, margen, metodo);
            if (precio.EsFallo)
            {
                return precio.Error;
            }

            conexos.Add(new clsServicioConexoCosteo(
                conexo.Id, conexo.Nombre, conexo.Categoria, conexo.Cantidad, conexo.ValorAplicado, precio.Valor,
                anteriores.TryGetValue(conexo.Id, out var anterior) ? anterior.Incluido : true));
        }

        return clsResultado<IReadOnlyList<clsServicioConexoCosteo>>.Exito(conexos);
    }

    /// <summary>
    /// <c>lineaId</c> de Nexo para una línea de Cotifly: el id de la línea de la cotización de Nexo con su <c>lineaSolicitudId</c>. Sin
    /// filas de línea (antes del 0005), la única línea de la cotización de Nexo. <c>null</c> si no hay (sin flete o sin id).
    /// </summary>
    public static string? LineaDeNexo(clsCotizacionNexo cotizacion, clsCotizacionLineaSolicitud? linea)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        IReadOnlyList<clsLineaCosteoNexo> candidatas = linea is null
            ? cotizacion.Lineas
            : [.. cotizacion.Lineas.Where(l => string.Equals(l.LineaSolicitudId, linea.LineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase))];
        return candidatas.Count == 1 && !string.IsNullOrWhiteSpace(candidatas[0].Id) ? candidatas[0].Id!.Trim() : null;
    }

    private static clsErrorDominio SinLineaNexo(clsCotizacion cotizacion, int orden) => new(clsCodigosError.LineaSinCotizacionNexo,
        $"La línea {orden} de la cotización {cotizacion.Folio} todavía no tiene línea en la cotización de Nexo: espera su costeo para agregarle conexos.");

    /// <summary>El error lleva <c>detalle.agregado_en_nexo = true</c>: el conexo ya está en Nexo y no se debe volver a agregar.</summary>
    private static clsErrorDominio AgregadoEnNexo(clsErrorDominio error)
    {
        var datos = new Dictionary<string, object?>(error.Datos ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            [ClaveAgregadoEnNexo] = true,
        };
        return error with { Datos = datos };
    }

    private static clsResultado<IReadOnlyList<clsConexoValidado>> Invalido(string mensaje) =>
        clsResultado<IReadOnlyList<clsConexoValidado>>.Fallo(new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida, mensaje));
}

/// <summary>Un conexo ya validado: la línea de Cotifly (<c>orden</c>), el servicio del catálogo de Nexo (UUID) y la cantidad.</summary>
public sealed record clsConexoValidado(int LineaOrden, string ServicioId, decimal Cantidad);
