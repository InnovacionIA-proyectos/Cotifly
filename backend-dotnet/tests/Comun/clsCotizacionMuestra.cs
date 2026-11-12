using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Solicitudes de prueba sobre el agregado de la cotización (cabecera → rutas → camiones): formulario de una ruta, licitación de N rutas y
/// cotización del chat, en el estado que pida la prueba. Todo pasa por el dominio (nada de reflexión).
/// </summary>
public static class clsCotizacionMuestra
{
    public static readonly DateTimeOffset Ahora = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);

    /// <summary>Cabecera sin rutas (en pesos), lista para <see cref="clsCotizacionSolicitud.RegistrarCaptura"/>.</summary>
    public static clsCotizacionSolicitud Cabecera(string folio = "PCN-AP-28092026-010") =>
        clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S." }).Valor;

    /// <summary>Formulario de una ruta y un camión, guardado (en <c>pendiente_envio</c>).</summary>
    public static clsCotizacionSolicitud Formulario(
        string folio = "PCN-AP-28092026-009", Guid? usuarioId = null, int? lineaNegocioId = 3, clsSolicitudCosteo? solicitud = null,
        string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio, UsuarioId = usuarioId, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.", Moneda = moneda, TrmAplicada = trm,
        }).Valor;
        cotizacion.RegistrarSolicitudCosteo(solicitud ?? clsSolicitudCosteoMuestra.Crear(), lineaNegocioId);
        return cotizacion;
    }

    /// <summary>
    /// Formulario enviado a Nexo (<c>SOL-1</c>) con margen (<paramref name="margen"/>, política) y la foto de Nexo aplicada: el camión
    /// con <paramref name="flete"/> (y <paramref name="retorno"/>) y la ruta con su precio. <paramref name="estado"/> <c>autocosteo</c> o
    /// <c>entregada</c>; con <c>null</c> en <paramref name="flete"/> queda en <c>pendiente_costeo</c>.
    /// </summary>
    public static clsCotizacionSolicitud Costeada(
        decimal? flete = 2_550_000m, decimal margen = 0.15m, EstadoCosteo estado = EstadoCosteo.Autocosteo, decimal? retorno = null,
        string folio = "PCN-AP-28092026-009", Guid? usuarioId = null, int? lineaNegocioId = 3, string moneda = clsCotizacionSolicitud.MonedaPesos,
        decimal trm = 0m, OrigenMargen origen = OrigenMargen.PoliticaLinea, string metodo = "margen", clsSolicitudCosteo? solicitud = null)
    {
        var cotizacion = Formulario(folio, usuarioId, lineaNegocioId, solicitud, moneda, trm);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, Ahora, Ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(origen == OrigenMargen.SapTm ? 0.15m : margen, margen, origen, metodo);
        }

        AplicarCosteo(cotizacion, flete is null ? [] : [flete.Value], estado, retorno);
        return cotizacion;
    }

    /// <summary>
    /// Licitación de <paramref name="rutas"/> rutas (un camión cada una), guardada en <c>pendiente_envio</c>. Con <paramref name="tipo"/>
    /// <c>Spot</c>, un formulario de varias rutas (los conexos no se admiten en licitaciones).
    /// </summary>
    public static clsCotizacionSolicitud Licitacion(
        int rutas = 2, string folio = "PCN-AP-28092026-010", Guid? usuarioId = null, int? lineaNegocioId = 3,
        string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m, TipoSolicitud tipo = TipoSolicitud.Licitacion)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio, UsuarioId = usuarioId, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.", Moneda = moneda, TrmAplicada = trm,
        }).Valor;
        var lineas = Enumerable.Range(1, rutas).Select(i => new clsLineaSolicitudCosteo
        {
            Origen = "Buenaventura",
            Destino = $"Destino {i}",
            OrigenMunicipioId = 76109,
            DestinoMunicipioId = 11000 + i,
            TipoVehiculo = "3S3",
            Carroceria = "FURGON",
            PesoKg = 10_000m + i,
        }).ToList();
        cotizacion.RegistrarSolicitudLicitacion(
            new clsEncabezadoSolicitudCosteo { Tipo = tipo }, lineas, [.. Enumerable.Range(0, rutas).Select(i => (int?)i)], lineaNegocioId);
        return cotizacion;
    }

    /// <summary>Licitación enviada, con margen y con el flete de cada ruta (en orden) aplicado.</summary>
    public static clsCotizacionSolicitud LicitacionCosteada(
        IReadOnlyList<decimal> fletes, decimal margen = 0.15m, EstadoCosteo estado = EstadoCosteo.Autocosteo, string folio = "PCN-AP-28092026-010",
        Guid? usuarioId = null, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m, TipoSolicitud tipo = TipoSolicitud.Licitacion)
    {
        ArgumentNullException.ThrowIfNull(fletes);
        var cotizacion = Licitacion(fletes.Count, folio, usuarioId, moneda: moneda, trm: trm, tipo: tipo);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, Ahora, Ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(margen, margen, OrigenMargen.PoliticaLinea, "margen");
        }

        AplicarCosteo(cotizacion, fletes, estado, null);
        return cotizacion;
    }

    /// <summary>Cotización del chat (sin Nexo): ruta con margen y precio, camión con el flete.</summary>
    public static clsCotizacionSolicitud Chat(
        decimal costo = 2_000_000m, decimal margen = 0.15m, decimal? precio = 2_352_941m, string folio = "PCN-AP-28092026-011", Guid? usuarioId = null,
        int? lineaNegocioId = 3, OrigenMargen origen = OrigenMargen.PoliticaLinea)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = usuarioId }).Valor;
        cotizacion.RegistrarCotizacionChat(lineaNegocioId, costo, margen, margen, origen, "margen", precio);
        return cotizacion;
    }

    /// <summary>
    /// Aplica a cada camión (en orden) su flete como si lo leyera el polling: id de línea de Nexo <c>L{n}</c>, el estado, el flete y el
    /// retorno; las rutas y la solicitud resumen su estado y se recalculan los totales y el precio.
    /// </summary>
    public static void AplicarCosteo(clsCotizacionSolicitud cotizacion, IReadOnlyList<decimal> fletes, EstadoCosteo estado, decimal? retorno)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(fletes);
        var camiones = cotizacion.Vehiculos;
        for (var i = 0; i < camiones.Count; i++)
        {
            camiones[i].AsignarLineaNexo($"L{i + 1}");
            if (i < fletes.Count)
            {
                camiones[i].RegistrarCosteo(estado, fletes[i], retorno);
            }
            else
            {
                camiones[i].RegistrarCosteo(EstadoCosteo.PendienteCosteo, null, null);
            }
        }

        var resumen = cotizacion.ResumirRutas() ?? estado;
        cotizacion.RegistrarConsultaCosteo(resumen, "COT-1", clsDerivacionEstadoCosteo.EsTerminal(resumen) ? null : Ahora.AddMinutes(1));
        cotizacion.RecalcularTotales(Ahora);
    }

    /// <summary>La foto de Nexo de una solicitud con una línea <c>L{n}</c> por flete (estado <c>EnCosteo</c>, cotización en borrador).</summary>
    public static clsCosteoConsultadoNexo Foto(IReadOnlyList<decimal> fletes, decimal? retorno = null, IReadOnlyList<clsConexoNexo>? conexos = null, string estadoCotizacion = "Borrador")
    {
        ArgumentNullException.ThrowIfNull(fletes);
        var lineas = fletes.Select((f, i) => $"L{i + 1}").ToList();
        return new clsCosteoConsultadoNexo(
            "EnCosteo",
            lineas,
            new clsCotizacionNexo("COT-1", estadoCotizacion, "SinDefinir",
                [.. fletes.Select((f, i) => new clsLineaCosteoNexo($"L{i + 1}", f, null, retorno, $"CL{i + 1}"))], conexos ?? []));
    }

    /// <summary>
    /// Agrega a la ruta <paramref name="orden"/> un conexo con su costo unitario de Nexo (<paramref name="costoUnitario"/>) y recalcula
    /// los totales y el precio. El precio de la ruta y de la solicitud lo incluye a su venta unitaria
    /// (<paramref name="ventaUnitaria"/>; <c>null</c> = al costo), sin margen.
    /// </summary>
    public static clsCotizacionSolicitud ConConexo(
        clsCotizacionSolicitud cotizacion, string idServicioConexo, string? nombre, decimal costoUnitario, decimal cantidad = 1m, int orden = 1,
        decimal? ventaUnitaria = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        cotizacion.Rutas.Single(r => r.Orden == orden)
            .AgregarOActualizarConexo(new clsCambioConexo(idServicioConexo, nombre, costoUnitario, cantidad, PrecioVenta: ventaUnitaria));
        cotizacion.RecalcularTotales(Ahora);
        return cotizacion;
    }

    /// <summary>Sube la versión de negocio (<c>row_version</c>) hasta <paramref name="version"/>, como lo harían los casos de uso.</summary>
    public static clsCotizacionSolicitud ConVersionNegocio(clsCotizacionSolicitud cotizacion, int version)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        while (cotizacion.VersionNegocio < version)
        {
            cotizacion.IncrementarVersionNegocio();
        }

        return cotizacion;
    }
}
