using System.Diagnostics;
using System.Globalization;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Servicios conexos del catálogo de Nexo (ADR-018 §4): <c>GET /web/servicios-conexos</c> (el buscador, sin costos) y
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
    /// <c>CONFLICTO_CONCURRENCIA</c> (409), <c>COSTEO_NO_LISTO</c> y <c>PDF_YA_ENVIADO</c>
    /// (409), <c>CONEXO_INVALIDO</c> (400, línea inexistente), <c>LINEA_SIN_COTIZACION_NEXO</c> (409) y los de Nexo. Si Nexo aceptó
    /// pero la relectura falla, <c>NEXO_NO_DISPONIBLE</c> con <c>agregado_en_nexo = true</c> (el polling lo recoge).
    /// <paramref name="claveIdempotencia"/> (<c>Idempotency-Key</c>, opcional): la repetición con la misma clave devuelve el resultado
    /// guardado (<see cref="IClsAlmacenIdempotencia"/>); la misma clave con otro cuerpo es <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (409).
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AgregarAsync(
        Guid usuarioId, string folio, IReadOnlyList<clsConexoAgregarEntrada>? items, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsServiciosConexosCosteoService"/>.
/// <list type="bullet">
/// <item><b>Línea de Nexo.</b> <c>linea_orden</c> → la ruta de Cotifly (<c>CotizacionLineasSolicitud.orden</c>) → su PRIMER camión
/// (los conexos de la ruta viajan pegados a la línea de su primer camión) → su <c>lineaSolicitudNexoId</c>
/// (guardado por el polling) → la línea de la cotización de Nexo con ese <c>lineaSolicitudId</c>, cuyo id se lee de Nexo justo antes de
/// agregar (<c>GET /api/cotizaciones/{id}</c>).</item>
/// <item><b>Precio.</b> Los conexos que Nexo agregó quedan como filas de la ruta (<c>CotizacionServiciosConexos</c>) con su
/// costo unitario de Nexo y su cantidad; la ruta los suma a su costo final y les aplica su margen (el precio de la ruta los incluye).</item>
/// <item><b>Concurrencia.</b> La versión se RESERVA en la base (sube y se guarda con el <c>If-Match</c>) antes del POST a Nexo: solo
/// una de varias peticiones simultáneas llega a Nexo; las demás reciben <c>CONFLICTO_CONCURRENCIA</c>. Los precios se guardan después
/// con la versión reservada. Todo 2xx de Nexo es efecto; sin respuesta, <c>agregado_en_nexo = "desconocido"</c>.</item>
/// </list>
/// </summary>
public sealed class clsServiciosConexosCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsServiciosConexosNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    IClsAlmacenIdempotencia idempotencia,
    IClsEnvioConexosNexoService envioConexos,
    ILogger<clsServiciosConexosCosteoService> logger,
    IClsCategoriasConexos categoriasConexos,
    IClsSituacionLineasNexo situacionesNexo,
    Margen.Services.IClsAvisoSincronizacionMargenSap avisoSincronizacion,
    IClsTarifaSeguroRepository tarifasSeguro,
    IClsPoliticaSegurosRepository politicaSeguros) : IClsServiciosConexosCosteoService
{
    // Las dependencias de la vista con estados son obligatorias: si falta un registro en DI, el contenedor falla al resolver el
    // servicio en lugar de devolver estado_nexo = null en silencio.

    /// <summary>Operación del ámbito de la idempotencia (<c>"conexos:{usuarioId}"</c>).</summary>
    public const string OperacionIdempotencia = "conexos";


    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "agregar_servicios_conexos";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Servicios conexos después del envío";

    /// <summary>Clave de <c>detalle</c> cuando Nexo ya agregó el conexo pero Cotifly no pudo terminar (contrato).</summary>
    public const string ClaveAgregadoEnNexo = "agregado_en_nexo";

    /// <summary>Valor de <c>agregado_en_nexo</c> cuando Nexo no respondió al POST y la relectura no confirma el conexo.</summary>
    public const string AgregadoDesconocido = "desconocido";

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

    public Task<clsResultado<clsCosteoCotizacion>> AgregarAsync(
        Guid usuarioId, string folio, IReadOnlyList<clsConexoAgregarEntrada>? items, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default) =>
        claveIdempotencia is null
            ? EjecutarAsync(usuarioId, folio, items, versionEsperada, cancellationToken)
            : idempotencia.EjecutarUnaVezAsync(
                $"{OperacionIdempotencia}:{usuarioId}",
                claveIdempotencia,
                Huella(folio, items),
                ct => EjecutarAsync(usuarioId, folio, items, versionEsperada, ct),
                SeGuarda,
                cancellationToken);

    /// <summary>
    /// Qué consume la <c>Idempotency-Key</c>: los éxitos y TODA respuesta cuyo POST a Nexo ya ocurrió o pudo ocurrir
    /// (<c>agregado_en_nexo</c> = <c>true</c> o <c>"desconocido"</c>): repetirla duplicaría el conexo en Nexo, que no tiene DELETE. Los
    /// demás errores no tocaron Nexo y la repetición se vuelve a evaluar.
    /// </summary>
    public static bool SeGuarda(clsResultado<clsCosteoCotizacion> resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return resultado.EsExitoso || resultado.Error.Datos?.ContainsKey(ClaveAgregadoEnNexo) == true;
    }

    /// <summary>Huella de la petición para la idempotencia: folio e items tal como llegaron (SHA-256).</summary>
    public static string Huella(string folio, IReadOnlyList<clsConexoAgregarEntrada>? items)
    {
        return clsHuellaIdempotencia.Calcular(new { folio = folio?.Trim(), items });
    }

    private async Task<clsResultado<clsCosteoCotizacion>> EjecutarAsync(
        Guid usuarioId, string folio, IReadOnlyList<clsConexoAgregarEntrada>? items, int versionEsperada, CancellationToken cancellationToken)
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
                items = validados.Valor.Select(i => new { linea_orden = i.LineaOrden, linea_solicitud_id = i.LineaSolicitudId, servicio_id = i.ServicioId, cantidad = i.Cantidad }).ToList(),
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
    /// Forma de los items (el esquema <c>AgregarServiciosConexosWeb</c>): de 1 a <see cref="MaximoItems"/>, cada uno con la ruta opcional
    /// (<c>linea_orden</c> ≥ 1 o <c>linea_solicitud_id</c> UUID), <c>servicio_id</c> UUID y <c>cantidad</c> de
    /// <see cref="CantidadMinima"/> a <see cref="CantidadMaxima"/>. Fuera de eso, <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>) sin
    /// tocar nada. La ruta la resuelve <see cref="RutaDe"/> con la cotización.
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

            if (item.LineaOrden is < 1)
            {
                return Invalido($"items[{i}].linea_orden debe ser 1 o mayor.");
            }

            Guid? rutaId = null;
            if (item.LineaSolicitudId is not null)
            {
                if (!Guid.TryParse(item.LineaSolicitudId, out var id))
                {
                    return Invalido($"items[{i}].linea_solicitud_id debe ser el id (UUID) de una ruta de la cotización.");
                }

                rutaId = id;
            }

            if (!Guid.TryParse(item.ServicioId, out var servicio))
            {
                return Invalido($"items[{i}].servicio_id debe ser el id (UUID) de un servicio del catálogo.");
            }

            if (item.Cantidad is not { } cantidad || cantidad < CantidadMinima || cantidad > CantidadMaxima || decimal.Round(cantidad, 2) != cantidad)
            {
                return Invalido($"items[{i}].cantidad debe estar entre {CantidadMinima.ToString(CultureInfo.InvariantCulture)} y {CantidadMaxima.ToString(CultureInfo.InvariantCulture)}, con hasta 2 decimales.");
            }

            validados.Add(new clsConexoValidado(item.LineaOrden, servicio.ToString("D"), cantidad, rutaId));
        }

        return clsResultado<IReadOnlyList<clsConexoValidado>>.Exito(validados);
    }

    /// <summary>
    /// Única fuente de las reglas de la cotización para agregar conexos, sin tocar Nexo (la usan el endpoint y la propuesta
    /// <c>agregar_servicios_conexos</c> del asistente; una licitación las sigue igual que una Spot), en este orden: costeo con
    /// margen (<c>COSTEO_NO_LISTO</c>), regla del PDF (<c>PDF_YA_ENVIADO</c>), la ruta (<see cref="RutaDe"/>: <c>RUTA_REQUERIDA</c>,
    /// <c>CONEXO_INVALIDO</c> si no existe, <c>SOLICITUD_INVALIDA</c> si los items son de rutas distintas) y ruta sin cotización de Nexo
    /// (<c>LINEA_SIN_COTIZACION_NEXO</c>). <c>null</c> si pasa.
    /// </summary>
    public static clsErrorDominio? ValidarPrecondiciones(clsCotizacionSolicitud cotizacion, IReadOnlyList<clsConexoValidado> items)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(items);
        if (!cotizacion.TieneCostoDisponible || cotizacion.RutasCotizables.Any(r => r.MargenAplicado is null || r.MetodoCalculo is null))
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado: espera el costeo para agregar conexos.");
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersion) is { } entregada)
        {
            return entregada;
        }

        // linea_orden / linea_solicitud_id → ruta de Cotifly.
        var ruta = RutaDe(cotizacion, items);
        if (ruta.EsFallo)
        {
            return ruta.Error;
        }

        // Una ruta rechazada por la Mesa de Costeo no tiene precio: no admite conexos (409 RUTA_RECHAZADA).
        if (cotizacion.ValidarRutaNoRechazada(ruta.Valor) is { } rechazada)
        {
            return rechazada;
        }

        return PrimerCamion(ruta.Valor)?.LineaSolicitudNexoId is null || cotizacion.CotizacionNexoId is null
            ? SinLineaNexo(cotizacion, ruta.Valor.Orden)
            : null;
    }

    /// <summary>
    /// La ruta a la que se agregan los conexos: cada item la indica con <c>linea_orden</c> o <c>linea_solicitud_id</c>
    /// (sin ninguno, la única de la cotización; con varias, <c>RUTA_REQUERIDA</c> con las rutas en el detalle). Una ruta que no existe:
    /// <c>CONEXO_INVALIDO</c> (<c>LINEA_INEXISTENTE</c>). Todos los items de una petición deben ser de la MISMA ruta
    /// (<c>SOLICITUD_INVALIDA</c>): los conexos nuevos de Nexo no dicen su línea y se guardan en la ruta pedida.
    /// </summary>
    public static clsResultado<clsLineaSolicitud> RutaDe(clsCotizacionSolicitud cotizacion, IReadOnlyList<clsConexoValidado> items)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(items);
        clsLineaSolicitud? ruta = null;
        foreach (var item in items)
        {
            var resuelta = cotizacion.ResolverRuta(item.LineaOrden, item.LineaSolicitudId);
            if (resuelta.EsFallo)
            {
                return resuelta.Error.Codigo == clsCodigosError.RutaInexistente
                    ? new clsErrorDominio(clsCodigosError.ConexoInvalido, resuelta.Error.Mensaje, "LINEA_INEXISTENTE")
                    : resuelta.Error;
            }

            if (ruta is not null && ruta.Id != resuelta.Valor.Id)
            {
                return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                    "Todos los items deben ser de la misma ruta: agrega los conexos de cada ruta en una petición aparte.");
            }

            ruta = resuelta.Valor;
        }

        return ruta is null
            ? new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida, $"'items' debe traer de 1 a {MaximoItems} servicios conexos.")
            : ruta;
    }

    /// <summary>
    /// Primer camión VIGENTE de la ruta, en el orden estable: el que lleva los conexos en Nexo. Un camión descartado (también
    /// el que perdió su línea porque la Mesa la borró) no recibe conexos: van al siguiente. Uno rechazado por la Mesa
    /// tampoco (su línea no tiene fila en la cotización de Nexo): van al primer camión no rechazado.
    /// </summary>
    public static clsVehiculoLineaSolicitud? PrimerCamion(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.VehiculosOrdenados.FirstOrDefault(v => v.EstadoCosteo is not (EstadoCosteo.Descartada or EstadoCosteo.Rechazada));
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, int Agregados, bool PdfInvalidado)> AgregarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, IReadOnlyList<clsConexoValidado> items, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), 0, false);
        }

        if (ValidarPrecondiciones(cotizacion, items) is { } noPermitido)
        {
            return (noPermitido, 0, false);
        }

        var cotizacionNexoId = cotizacion.CotizacionNexoId!;
        var ruta = RutaDe(cotizacion, items).Valor;

        // El id de la línea de la cotización de Nexo se lee de Nexo justo antes de agregar (ver el resumen de la clase).
        var actual = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, cancellationToken);
        if (actual.EsFallo)
        {
            return (actual.Error, 0, false);
        }

        var paraNexo = new List<clsConexoParaNexo>();
        foreach (var item in items)
        {
            var lineaId = LineaDeNexo(actual.Valor, PrimerCamion(ruta));
            if (lineaId is null)
            {
                return (SinLineaNexo(cotizacion, ruta.Orden), 0, false);
            }

            paraNexo.Add(new clsConexoParaNexo(lineaId, item.ServicioId, item.Cantidad));
        }

        // Reserva de la versión EN LA BASE antes de Nexo (ADR-018 §13): de N peticiones con el mismo If-Match solo una gana y llama al
        // POST (que no es idempotente); las demás reciben CONFLICTO_CONCURRENCIA sin tocar Nexo, con o sin Idempotency-Key, en
        // cualquier réplica y tras un reinicio.
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), 0, false);
        }

        var reservada = cotizacion.VersionNegocio;

        // Sin el token del cliente: un POST no se corta a mitad.
        var agregado = await nexo.AgregarAsync(usuarioId, cotizacionNexoId, paraNexo, CancellationToken.None);
        if (agregado.EsFallo)
        {
            return (agregado.Error, 0, false); // Nexo no lo aplicó: solo la versión reservada cambió
        }

        // Desde aquí el conexo existe (o pudo existir) en Nexo: no se vuelve a llamar al POST pase lo que pase.
        var cotizacionNexo = agregado.Valor.Cotizacion;
        string? detalleRelectura = agregado.Valor.Detalle;
        if (cotizacionNexo is null)
        {
            var releida = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, CancellationToken.None);
            if (releida.EsExitoso && (agregado.Valor.Seguro || TieneConexosNuevos(actual.Valor, releida.Valor)))
            {
                cotizacionNexo = releida.Valor;
            }
            else
            {
                detalleRelectura = releida.EsFallo ? releida.Error.Detalle ?? releida.Error.Codigo : "la cotización de Nexo no muestra el conexo";
            }
        }

        if (cotizacionNexo is null)
        {
            return await SinRelecturaAsync(
                cotizacion, usuarioId, reservada, agregado.Valor.Seguro ? (object)true : AgregadoDesconocido, detalleRelectura,
                new clsConexosPendientesNexo(cotizacion.Folio, ruta.Id, [.. actual.Valor.Conexos.Select(c => c.Id)]));
        }

        // Los conexos que Nexo agregó en esta petición quedan como filas de SU ruta (la del camión cuya línea los lleva; si no
        // se sabe, la ruta pedida) con el costo unitario de Nexo y la cantidad, y esa ruta y la cabecera se recalculan con el
        // margen de la ruta.
        var importados = ImportarConexosPorRuta(
            cotizacion, cotizacionNexo, NuevosConexos(actual.Valor, cotizacionNexo), ruta, cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt);
        if (importados.EsFallo)
        {
            return (AgregadoEnNexo(importados.Error, true), 0, false);
        }

        var agregados = importados.Valor;

        // El codigoServicio de los conexos agregados (la cotización de Nexo no lo trae), con el índice del catálogo que ya
        // tiene el costeo en memoria, por el id del catálogo. No se lee el catálogo de Nexo aquí (ni se llena la caché de costos, que
        // decide qué ve la versión nueva): si el índice está frío se calienta en segundo plano y el código se completa al leer el costeo.
        await clsPoliticaSegurosCosteo.CompletarCodigosAsync(categoriasConexos, cotizacion, logger, CancellationToken.None);

        // Nexo ya tiene los conexos (los creó este POST): quedan enviados en el registro del envío diferido, en la misma escritura.
        // Si no, estado_nexo = pendiente bloquearía finalizar, y en una entregada el polling ya no pasa para confirmarlos.
        try
        {
            await envioConexos.RegistrarConfirmadosAsync(cotizacion, cotizacionNexo, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo registrar como enviados a Nexo los conexos agregados en {Folio}; el polling los confirma.", cotizacion.Folio);
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, reservada, CancellationToken.None))
        {
            // Otro cambio (el polling) ganó después de la reserva: el conexo ya está en Nexo y la siguiente consulta lo recoge.
            return (AgregadoEnNexo(clsReglaPdfCosteo.Conflicto(cotizacion.Folio), true), 0, false);
        }

        // La misma vista que GET .../costeo y los PUT: con el estado_nexo de cada conexo, la categoría del
        // catálogo y la situación de las líneas en Nexo. Ya se guardó: sin el token del cliente.
        return (await clsCosteoCotizacionService.VistaConEstadosAsync(
            cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, logger, CancellationToken.None, categoriasConexos,
            situacionesNexo.Obtener(cotizacion.Id),
            await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, CancellationToken.None),
            tarifasSeguro, politicaSeguros),
            agregados, pdfInvalidado);
    }

    /// <summary>¿La cotización releída tiene conexos que no estaban antes del POST? (confirma un efecto incierto).</summary>
    public static bool TieneConexosNuevos(clsCotizacionNexo antes, clsCotizacionNexo despues) => NuevosConexos(antes, despues).Count > 0;

    /// <summary>Conexos de <paramref name="despues"/> que no estaban en <paramref name="antes"/> (por id de Nexo).</summary>
    public static IReadOnlyList<clsConexoNexo> NuevosConexos(clsCotizacionNexo antes, clsCotizacionNexo despues)
    {
        ArgumentNullException.ThrowIfNull(antes);
        ArgumentNullException.ThrowIfNull(despues);
        var previos = antes.Conexos.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. despues.Conexos.Where(c => !previos.Contains(c.Id))];
    }

    /// <summary>
    /// Guarda <paramref name="conexos"/> de Nexo como filas de la ruta: costo unitario = <c>valorAplicado ÷ cantidad</c> (el mismo
    /// criterio del 0009, columna <c>precio</c>), la cantidad y el nombre. La venta unitaria (columna <c>precioVenta</c>) es
    /// <see cref="clsConexoNexo.VentaUnitaria"/>. Un conexo que ya estaba se actualiza. La raíz recalcula siempre los totales y el precio
    /// (<see cref="clsCotizacionSolicitud.CambiarConexos"/>). Devuelve cuántos agregó o cambió, o el error del módulo de precio.
    /// </summary>
    public static clsResultado<int> ImportarConexos(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IEnumerable<clsConexoNexo> conexos, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        ArgumentNullException.ThrowIfNull(conexos);
        var cambios = conexos.Where(c => c.ValorAplicado >= 0m).Select(conexo =>
        {
            var cantidad = conexo.Cantidad > 0m ? decimal.Round(conexo.Cantidad, 2) : 1m;
            var precio = decimal.Round(conexo.ValorAplicado / cantidad, 2, MidpointRounding.ToEven);
            // Con el id del catálogo (servicioConexoId, script 03) y la categoría que informó Nexo (script 05).
            return new clsCambioConexo(
                conexo.Id, conexo.Nombre ?? conexo.Categoria, precio, cantidad, conexo.ServicioConexoId, conexo.CategoriaInformada,
                conexo.VentaUnitaria);
        }).ToList();
        return cotizacion.CambiarConexos(ruta.Orden, cambios, ahora);
    }

    /// <summary>
    /// La venta que Nexo fijó para los conexos que Cotifly YA tiene guardados (algunas filas tienen
    /// <c>precioVenta = precio</c>). Se cruza por el id: la fila de Nexo cuyo <c>id</c> es el <c>idServicioConexo</c> guardado (conexo
    /// agregado o importado) o, si no hay, las de su <c>servicioConexoId</c> (el id del catálogo de un conexo capturado) que son de la misma
    /// ruta (<see cref="RutaDelConexo"/>) y que no son ya, por id, de otro conexo guardado. Solo con una venta conocida (<see cref="clsConexoNexo.VentaUnitaria"/>) y única: sin venta, o con
    /// dos filas de distinta venta para el mismo servicio, el conexo conserva la suya.
    /// </summary>
    public static IReadOnlyList<clsVentaConexo> VentasDeNexo(clsCotizacionSolicitud cotizacion, clsCotizacionNexo cotizacionNexo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        var ventas = new List<clsVentaConexo>();
        // Filas de Nexo que ya son, por id, de un conexo guardado: no cuentan en el cruce por servicio de los demás.
        var conDueno = cotizacion.Rutas.SelectMany(r => r.Conexos).Select(c => c.IdServicioConexo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ruta in cotizacion.Rutas)
        {
            foreach (var conexo in ruta.Conexos)
            {
                var filas = cotizacionNexo.Conexos.Where(n => string.Equals(n.Id, conexo.IdServicioConexo, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filas.Count == 0)
                {
                    var catalogo = conexo.IdServicioConexoCatalogo ?? conexo.IdServicioConexo;
                    filas = [.. cotizacionNexo.Conexos.Where(n =>
                        !conDueno.Contains(n.Id)
                        && string.Equals(n.ServicioConexoId, catalogo, StringComparison.OrdinalIgnoreCase)
                        && RutaDelConexo(cotizacion, cotizacionNexo, n)?.Id == ruta.Id)];
                }

                var distintas = filas.Select(n => n.VentaUnitaria).OfType<decimal>().Distinct().ToList();
                if (distintas.Count == 1)
                {
                    ventas.Add(new clsVentaConexo(ruta.Id, conexo.IdServicioConexo, distintas[0]));
                }
            }
        }

        return ventas;
    }

    /// <summary>
    /// Ruta de Cotifly de un conexo de Nexo: los conexos viajan pegados a la línea del primer camión de su ruta, así
    /// que <c>cotizacionLineaId</c> → la línea de la cotización de Nexo → su <c>lineaSolicitudId</c> → el camión con ese
    /// <c>lineaSolicitudNexoId</c> → su ruta. <c>null</c> si no se puede seguir la cadena (Nexo no trae la línea o el camión no está asociado).
    /// </summary>
    public static clsLineaSolicitud? RutaDelConexo(clsCotizacionSolicitud cotizacion, clsCotizacionNexo cotizacionNexo, clsConexoNexo conexo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        ArgumentNullException.ThrowIfNull(conexo);
        var lineaSolicitud = cotizacionNexo.Lineas
            .FirstOrDefault(l => conexo.CotizacionLineaId is not null && string.Equals(l.Id, conexo.CotizacionLineaId, StringComparison.OrdinalIgnoreCase))
            ?.LineaSolicitudId;
        return lineaSolicitud is null
            ? null
            : cotizacion.Rutas.FirstOrDefault(r => r.Vehiculos.Any(v => string.Equals(v.LineaSolicitudNexoId, lineaSolicitud, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Importa <paramref name="conexos"/> de Nexo, cada uno a su ruta (<see cref="RutaDelConexo"/>) o, si no se puede saber, a
    /// <paramref name="porDefecto"/>. Devuelve cuántos agregó o cambió, o el primer error del módulo de precio.
    /// </summary>
    public static clsResultado<int> ImportarConexosPorRuta(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo cotizacionNexo, IEnumerable<clsConexoNexo> conexos, clsLineaSolicitud porDefecto, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        ArgumentNullException.ThrowIfNull(conexos);
        ArgumentNullException.ThrowIfNull(porDefecto);
        var total = 0;
        foreach (var grupo in conexos.GroupBy(c => RutaDelConexo(cotizacion, cotizacionNexo, c) ?? porDefecto).OrderBy(g => g.Key.Orden))
        {
            var importados = ImportarConexos(cotizacion, grupo.Key, grupo, ahora);
            if (importados.EsFallo)
            {
                return importados.Error;
            }

            total += importados.Valor;
        }

        return total;
    }

    /// <summary>
    /// No hay cotización de Nexo con el conexo: Nexo lo agregó pero no se pudo leer (<paramref name="agregado"/> = <c>true</c>) o no se
    /// sabe si lo agregó (<see cref="AgregadoDesconocido"/>: tiempo agotado, red o 5xx sin confirmación). 503 <c>NEXO_NO_DISPONIBLE</c>
    /// con <c>detalle.agregado_en_nexo</c>; nunca se repite el POST. Si había PDF vigente se invalida (el polling no toca los conexos de
    /// una cotización con PDF) con la versión ya reservada; sin PDF el polling recoge el conexo en su siguiente consulta.
    /// </summary>
    private async Task<(clsResultado<clsCosteoCotizacion>, int, bool)> SinRelecturaAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int reservada, object agregado, string? detalle, clsConexosPendientesNexo pendientes)
    {
        // El polling no importa los conexos de Nexo (Cotifly es la fuente) salvo con esta marca. Junto a la marca, y en la misma transacción, se guardan la ruta pedida y los ids que Nexo ya tenía ANTES del POST: la siguiente
        // lectura trae a ESA ruta solo los conexos nuevos (nunca uno que el comercial quitó en Cotifly ni uno de otra ruta).
        cotizacion.MarcarConexosPendientesDeLeer();
        cotizaciones.RegistrarConexosPendientesNexo(pendientes);
        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        pdfInvalidado = await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, reservada, CancellationToken.None) && pdfInvalidado;
        var mensaje = agregado is true
            ? "Nexo agregó el servicio conexo pero no se pudo leer de nuevo la cotización: aparecerá con su precio en la siguiente actualización. No lo vuelvas a agregar."
            : "Nexo no respondió y no se sabe si agregó el servicio conexo: revisa el costeo en unos minutos antes de volver a agregarlo.";
        return (AgregadoEnNexo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, mensaje, detalle), agregado), 0, pdfInvalidado);
    }

    /// <summary>
    /// <c>lineaId</c> de Nexo para un camión de Cotifly: el id de la línea de la cotización de Nexo con su <c>lineaSolicitudId</c>.
    /// Basta con UNA; si la Mesa dejó varias para el mismo camión (el costo ya las suma, <c>clsCosteoConsultadoNexo.CostoLinea</c>) se toma
    /// la primera por id (orden ordinal), un criterio estable para el envío diferido y para el POST. Sin camión, la única línea de la
    /// cotización de Nexo. <c>null</c> si no hay (sin flete o sin id).
    /// </summary>
    public static string? LineaDeNexo(clsCotizacionNexo cotizacion, clsVehiculoLineaSolicitud? camion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (camion is null)
        {
            return cotizacion.Lineas.Count == 1 && !string.IsNullOrWhiteSpace(cotizacion.Lineas[0].Id) ? cotizacion.Lineas[0].Id!.Trim() : null;
        }

        if (string.IsNullOrWhiteSpace(camion.LineaSolicitudNexoId))
        {
            return null;
        }

        return cotizacion.Lineas
            .Where(l => string.Equals(l.LineaSolicitudId, camion.LineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(l.Id))
            .Select(l => l.Id!.Trim())
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static clsErrorDominio SinLineaNexo(clsCotizacionSolicitud cotizacion, int orden) => new(clsCodigosError.LineaSinCotizacionNexo,
        $"La línea {orden} de la cotización {cotizacion.Folio} todavía no tiene línea en la cotización de Nexo: espera su costeo para agregarle conexos.");

    /// <summary>
    /// El error lleva <c>detalle.agregado_en_nexo</c>: <c>true</c> (el conexo ya está en Nexo) o <see cref="AgregadoDesconocido"/>. En
    /// ambos casos no se debe volver a agregar sin revisar el costeo.
    /// </summary>
    private static clsErrorDominio AgregadoEnNexo(clsErrorDominio error, object agregado)
    {
        var datos = new Dictionary<string, object?>(error.Datos ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            [ClaveAgregadoEnNexo] = agregado,
        };
        return error with { Datos = datos };
    }

    private static clsResultado<IReadOnlyList<clsConexoValidado>> Invalido(string mensaje) =>
        clsResultado<IReadOnlyList<clsConexoValidado>>.Fallo(new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida, mensaje));
}

/// <summary>
/// Un conexo ya validado en su forma: la ruta de Cotifly como llegó (<c>linea_orden</c> o <c>linea_solicitud_id</c>, o ninguno; la
/// resuelve <see cref="clsServiciosConexosCosteoService.RutaDe"/>), el servicio del catálogo de Nexo (UUID) y la cantidad.
/// </summary>
public sealed record clsConexoValidado(int? LineaOrden, string ServicioId, decimal Cantidad, Guid? LineaSolicitudId = null);
