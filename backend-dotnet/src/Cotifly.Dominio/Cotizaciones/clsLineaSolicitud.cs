using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Ruta de una solicitud (<c>dbo_cotifly.CotizacionLineasSolicitud</c>; scripts 0005, 0009 y 0010; HU-021, ADR-019): lo que es igual
/// para todos sus camiones (origen y destino, condiciones del contenedor, horas, valores), sus mercancías y conexos, los totales de
/// costo, el margen de su línea de negocio y su precio. Los costos son por camión (<see cref="Vehiculos"/>) y el precio es por ruta:
/// <list type="bullet">
/// <item><see cref="CostoFletesTotal"/>, <see cref="CostoRetornoContenedorTotal"/> y <see cref="CostoLevanteContenedorTotal"/> suman
/// sus camiones; <see cref="CostoServiciosConexos"/> suma <c>precio × cantidad</c> de sus conexos;</item>
/// <item><see cref="CostoFinal"/> = la suma de los cuatro, y <see cref="PrecioFinalLinea"/> = <see cref="CostoFinal"/> con
/// <see cref="MargenAplicado"/> según <see cref="MetodoCalculo"/> (la fórmula de <see cref="clsCalculadoraPrecio"/>);</item>
/// <item>mientras algún camión no tenga flete, la ruta no tiene costo final ni precio.</item>
/// </list>
/// Los totales se GUARDAN y se recalculan con cada cambio (<see cref="RecalcularTotales"/>). El estado resume el de sus camiones
/// (<see cref="clsResumenEstadoCosteo"/>). Los costos son internos: el comercial ve el precio. <see cref="CreatedAt"/> y
/// <see cref="UpdatedAt"/> los asigna la persistencia.
/// </summary>
public sealed partial class clsLineaSolicitud
{
    public const string CodigoError = clsCodigosError.SolicitudCosteoInvalida;
    public const int LargoMaximoNombreMunicipio = 150;

    private readonly List<clsVehiculoLineaSolicitud> _vehiculos = [];
    private readonly List<clsMercancia> _mercancias = [];
    private readonly List<clsServicioConexoRuta> _conexos = [];

    private clsLineaSolicitud()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Solicitud a la que pertenece (ex <c>cotizacionId</c>).</summary>
    public Guid CotizacionSolicitudId { get; private set; }

    /// <summary>1 para la primera ruta de la solicitud (único por solicitud).</summary>
    public int Orden { get; private set; }

    /// <summary>Fila del Excel de la licitación de la que salió (base 0); 0 en una cotización simple (ex <c>filaOrigen</c>).</summary>
    public int? RutaOrigenIndice { get; private set; }

    /// <summary>Línea de negocio de la ruta (FK a <c>MargenesPolitica</c>): define su margen y su método.</summary>
    public int? LineaNegocioId { get; private set; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo del origen.</summary>
    public int? OrigenMunicipioId { get; private set; }

    /// <summary>Nombre del municipio de origen, copiado al capturar (0010, decisión 2 del 2026-10-08).</summary>
    public string? OrigenNombre { get; private set; }

    public int? DestinoMunicipioId { get; private set; }

    /// <summary>Nombre del municipio de destino, copiado al capturar (0010).</summary>
    public string? DestinoNombre { get; private set; }

    public string? Tipologia { get; private set; }
    public string? ClaseViaje { get; private set; }
    public string? ClaseContenedor { get; private set; }
    public bool? Exclusivo { get; private set; }
    public bool? Consolidado { get; private set; }

    /// <summary>¿El contenedor se devuelve? Siempre sí o no (0009: <c>NOT NULL DEFAULT 0</c>).</summary>
    public bool RetornaContenedor { get; private set; }

    public int? CiudadDevolucionMunicipioId { get; private set; }

    /// <summary>Nombre del municipio donde se devuelve el contenedor (0010).</summary>
    public string? CiudadDevolucionNombre { get; private set; }

    /// <summary>¿El contenedor se levanta en otra ciudad? Siempre sí o no.</summary>
    public bool LevanteContenedorDiferente { get; private set; }

    public int? CiudadLevanteMunicipioId { get; private set; }

    /// <summary>Nombre del municipio donde se levanta el contenedor (0010).</summary>
    public string? CiudadLevanteNombre { get; private set; }

    public decimal? HorasCargue { get; private set; }
    public decimal? HorasDescargue { get; private set; }

    /// <summary>Peso total que declaró el comercial para la ruta.</summary>
    public decimal? PesoKg { get; private set; }

    /// <summary>Cuánto vale la mercancía de la ruta (no es un costo).</summary>
    public decimal? ValorMercancia { get; private set; }

    /// <summary>
    /// HU-030.1 (#184, script <c>db/scripts/11_peso_con_tara.sql</c>): peso total de la ruta CON la tara de los contenedores, tal como lo
    /// escribió el comercial (solo en una ruta <c>Contenedor</c>); <c>null</c> = sin tara. Es lo que viaja a Nexo como peso (repartido por
    /// camión, <see cref="PesoLineaNexo"/>); al cubicador nunca va.
    /// </summary>
    public decimal? PesoConTaraKg { get; private set; }

    /// <summary>Lo que vale UN levante de contenedor en esta ruta (lo digita el comercial; se copia a cada camión, decisión 4).</summary>
    public decimal? ValorLevanteContenedor { get; private set; }

    public decimal? CostoFletesTotal { get; private set; }
    public decimal? CostoRetornoContenedorTotal { get; private set; }
    public decimal? CostoLevanteContenedorTotal { get; private set; }
    public decimal? CostoServiciosConexos { get; private set; }

    /// <summary>Fletes + retorno + levante + conexos; <c>null</c> mientras algún camión no tenga flete.</summary>
    public decimal? CostoFinal { get; private set; }

    /// <summary>Margen de la política de su línea de negocio al costear (referencia del ajuste).</summary>
    public decimal? MargenPoliticaSugerido { get; private set; }

    /// <summary>Margen negociado en SAP TM, si aplicó (entonces es el sugerido vigente).</summary>
    public decimal? MargenSAPSugerido { get; private set; }

    public decimal? MargenAplicado { get; private set; }
    public OrigenMargen? OrigenMargen { get; private set; }
    public string? MetodoCalculo { get; private set; }

    /// <summary>Precio de la ruta al cliente (ex <c>precioFinal</c> de la línea), en la moneda de la solicitud.</summary>
    public decimal? PrecioFinalLinea { get; private set; }

    /// <summary>HU-023.3 (script <c>02</c>): la corrida del cubicador de esta ruta (para volver a consultarla); <c>null</c> sin cubicador.</summary>
    public Guid? IdRecomendacion { get; private set; }

    /// <summary>HU-023.3: qué respondió el cubicador (<c>ASIGNADO</c> o <c>SIN_SELECCION_AUTOMATICA</c>); <c>null</c> sin cubicador.</summary>
    public string? ResultadoCubicacion { get; private set; }

    /// <summary>HU-023.3: por qué no pudo escoger (solo con <c>SIN_SELECCION_AUTOMATICA</c>).</summary>
    public string? MotivoSinSeleccion { get; private set; }

    /// <summary>HU-023.3: la ruta va a la Mesa de Costeo por una mercancía condicionada.</summary>
    public bool Condicionada { get; private set; }

    /// <summary>
    /// HU-023.3: avisos del cubicador de la última llamada (mercancías condicionadas, advertencias y extradimensión). HU-023.5 (#160):
    /// se guardan en <c>avisosCubicacion</c> (JSON, propuesta <c>db/scripts/pendientes/04</c>) porque la licitación se cubica en segundo plano y el comercial
    /// los lee después en <c>GET /web/licitaciones/{folio}</c>. <c>null</c> sin cubicador (o en una ruta anterior al 04).
    /// </summary>
    public clsAvisosCubicacion? AvisosCubicacion { get; private set; }

    /// <summary>
    /// HU-023.5 (#160): estado de la cubicación en segundo plano (<c>null</c> = se cubicó al guardarla). Ver <see cref="Cotizaciones.EstadoCubicacion"/>.
    /// </summary>
    public EstadoCubicacion? EstadoCubicacion { get; private set; }

    /// <summary>HU-023.5: intentos fallidos de cubicar la ruta en segundo plano (se reinicia al reintentar a mano).</summary>
    public int CubicacionIntentos { get; private set; }

    /// <summary>
    /// HU-023.5: desde cuándo una réplica puede tomar la ruta pendiente (<c>null</c> = ya). Sirve de reclamo (una réplica la toma hasta
    /// <c>ahora + Cubicador:ReclamoSegundos</c>) y de espera entre reintentos.
    /// </summary>
    public DateTimeOffset? CubicacionDisponibleAt { get; private set; }

    /// <summary>HU-023.5: último error de la cubicación en segundo plano (<c>CODIGO: mensaje</c>); <c>null</c> si no falló.</summary>
    public string? ErrorCubicacion { get; private set; }

    /// <summary>Largo máximo de <see cref="ErrorCubicacion"/> (columna <c>errorCubicacion NVARCHAR(500)</c>).</summary>
    public const int LargoMaximoErrorCubicacion = 500;

    /// <summary>Resumen del estado de sus camiones (<see cref="clsResumenEstadoCosteo"/>).</summary>
    public EstadoCosteo? EstadoCosteo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Camiones de la ruta, en el orden en que se crearon.</summary>
    public IReadOnlyList<clsVehiculoLineaSolicitud> Vehiculos => _vehiculos;

    /// <summary>Mercancías de la ruta (ordenadas por <see cref="clsMercancia.Orden"/> al leerlas).</summary>
    public IReadOnlyList<clsMercancia> Mercancias => _mercancias;

    /// <summary>Servicios conexos de la ruta.</summary>
    public IReadOnlyList<clsServicioConexoRuta> Conexos => _conexos;

    /// <summary>
    /// Camiones en un orden estable (<see cref="clsVehiculoLineaSolicitud.EnOrden"/>: <c>ordenVehiculo</c>, o <c>createdAt</c> en las filas
    /// anteriores a #188): el primero lleva los conexos en Nexo (decisión 3).
    /// </summary>
    public IReadOnlyList<clsVehiculoLineaSolicitud> VehiculosOrdenados => [.. clsVehiculoLineaSolicitud.EnOrden(_vehiculos)];

    /// <summary>Mercancías en su orden de captura.</summary>
    public IReadOnlyList<clsMercancia> MercanciasOrdenadas => [.. _mercancias.OrderBy(m => m.Orden)];

    /// <summary><c>"Origen → Destino"</c> con los nombres copiados (o los ids si faltan).</summary>
    public string Ruta => $"{OrigenNombre ?? OrigenMunicipioId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} → "
                          + $"{DestinoNombre ?? DestinoMunicipioId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}";

    /// <summary>
    /// Ruta nueva a partir de una línea de la captura ya validada (<see cref="clsLineaSolicitudCosteo.Errores"/> vacía). Las horas son las
    /// de la línea o, si no trae, las del <paramref name="encabezado"/> (la cabecera ya no las guarda). Copia las mercancías; los camiones
    /// los agrega quien crea la ruta (<see cref="AgregarVehiculo"/>). <paramref name="id"/> fija el id de la ruta (el que ya conoce el
    /// cubicador, HU-021.5); sin él, uno nuevo.
    /// </summary>
    public static clsLineaSolicitud Desde(
        Guid cotizacionSolicitudId, int orden, int? rutaOrigenIndice, int? lineaNegocioId, clsLineaSolicitudCosteo linea,
        clsEncabezadoSolicitudCosteo? encabezado = null, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(linea);
        ArgumentOutOfRangeException.ThrowIfLessThan(orden, 1);
        if (rutaOrigenIndice is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rutaOrigenIndice), "El índice de la fila de origen no puede ser negativo.");
        }

        var ruta = new clsLineaSolicitud
        {
            Id = id is { } fijo && fijo != Guid.Empty ? fijo : Guid.NewGuid(),
            CotizacionSolicitudId = cotizacionSolicitudId,
            Orden = orden,
            RutaOrigenIndice = rutaOrigenIndice,
            LineaNegocioId = lineaNegocioId,
            OrigenMunicipioId = linea.OrigenMunicipioId,
            OrigenNombre = NombreMunicipio(linea.Origen),
            DestinoMunicipioId = linea.DestinoMunicipioId,
            DestinoNombre = NombreMunicipio(linea.Destino),
            Tipologia = Normalizar(linea.Tipologia),
            ClaseViaje = Normalizar(linea.ClaseViaje),
            ClaseContenedor = Normalizar(linea.ClaseContenedor),
            Exclusivo = linea.Exclusivo,
            Consolidado = linea.Consolidado,
            RetornaContenedor = linea.RetornaContenedor ?? false,
            CiudadDevolucionMunicipioId = linea.CiudadDevolucionMunicipioId,
            CiudadDevolucionNombre = NombreMunicipio(linea.CiudadDevolucionContenedor),
            LevanteContenedorDiferente = linea.LevanteContenedorDiferente ?? false,
            CiudadLevanteMunicipioId = linea.CiudadLevanteMunicipioId,
            CiudadLevanteNombre = NombreMunicipio(linea.CiudadLevanteContenedor),
            HorasCargue = linea.HorasCargue ?? encabezado?.HorasCargue,
            HorasDescargue = linea.HorasDescargue ?? encabezado?.HorasDescargue,
            PesoKg = linea.PesoKg,
            ValorMercancia = linea.ValorMercancia,
            PesoConTaraKg = linea.PesoConTaraKg,
            ValorLevanteContenedor = linea.ValorAprovisionamiento,
            CostoServiciosConexos = 0m,
        };
        ruta._mercancias.AddRange(clsMercancia.DesdeCaptura(ruta.Id, linea.Posiciones));
        return ruta;
    }

    /// <summary>
    /// Ruta 1 de una cotización del agente (chat, sin Nexo; <c>CAMBIOS_BACKEND</c> §2): el costo del flete va en un camión y el margen y
    /// el precio en la ruta, tal como los calculó el módulo de Precio. Sin estado de costeo (no pasa por Nexo).
    /// </summary>
    public static clsLineaSolicitud DelChat(
        Guid cotizacionSolicitudId, int? lineaNegocioId, decimal? costo, decimal? margenPolitica, decimal? margenAplicado,
        OrigenMargen? origen, string? metodoCalculo, decimal? precioFinal)
    {
        if (costo is < 0m || precioFinal is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(costo), "El costo y el precio no pueden ser negativos.");
        }

        var ruta = new clsLineaSolicitud
        {
            Id = Guid.NewGuid(),
            CotizacionSolicitudId = cotizacionSolicitudId,
            Orden = 1,
            RutaOrigenIndice = 0,
            LineaNegocioId = lineaNegocioId,
            MargenAplicado = margenAplicado,
            OrigenMargen = origen,
            MetodoCalculo = Normalizar(metodoCalculo),
            CostoServiciosConexos = 0m,
        };
        ruta.FijarSugeridos(margenPolitica, margenAplicado, origen);
        var camion = ruta.AgregarVehiculo(null, null, null);
        if (costo is { } flete)
        {
            camion.RegistrarCostoChat(flete);
        }

        ruta.SumarCostos();
        ruta.PrecioFinalLinea = precioFinal;
        return ruta;
    }

    /// <summary>
    /// Camión nuevo de la ruta con el levante de la ruta copiado (decisión 4; 0 si no aplica). Vehículo y carrocería vacíos = sin
    /// selección automática (la Mesa de Costeo los resuelve). Recalcula los totales de levante.
    /// </summary>
    public clsVehiculoLineaSolicitud AgregarVehiculo(string? tipoVehiculo, string? carroceria, decimal? pesoKg, Guid? idRecomendacionVehiculo = null) =>
        AgregarVehiculo(new clsCamionPlaneado(tipoVehiculo, carroceria, pesoKg, idRecomendacionVehiculo));

    /// <summary>
    /// Camión nuevo de la ruta a partir del plan (HU-023.3: con el volumen y los porcentajes de llenado del cubicador) y con el levante de
    /// la ruta copiado. HU-014 (#188): el camión queda numerado en <see cref="clsVehiculoLineaSolicitud.OrdenVehiculo"/> después de los que
    /// ya tiene la ruta (1..n en una ruta nueva o recalculada, porque <see cref="ReemplazarCamiones"/> la vacía antes); nunca repite un
    /// número, así que respeta el índice único <c>(lineaSolicitudId, ordenVehiculo)</c> del script 03. En una ruta con camiones sin número
    /// (escritos antes de #188) queda también sin número: así se ordena por <c>createdAt</c>, detrás de los que ya tiene.
    /// </summary>
    public clsVehiculoLineaSolicitud AgregarVehiculo(clsCamionPlaneado plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int? siguiente = _vehiculos.Any(v => v.OrdenVehiculo is null)
            ? null
            : Math.Max(_vehiculos.Count, _vehiculos.Max(v => v.OrdenVehiculo) ?? 0) + 1;
        var camion = clsVehiculoLineaSolicitud.Crear(
            Id, plan.TipoVehiculo, plan.Carroceria, plan.PesoKg, ValorLevanteContenedor, plan.IdRecomendacionVehiculo, siguiente,
            plan.VolumenTotalM3, plan.PorcentajeLlenadoVolumen, plan.PorcentajeLlenadoPeso);
        _vehiculos.Add(camion);
        SumarCostos();
        return camion;
    }

    /// <summary>
    /// HU-023.3: guarda lo que respondió el cubicador para la ruta (<c>null</c> = sin cubicador: se limpian el resultado, el motivo y la
    /// corrida). Los avisos se guardan con la ruta (<see cref="AvisosCubicacion"/>, HU-023.5).
    /// </summary>
    public void RegistrarCubicacion(clsCubicacionRuta? cubicacion)
    {
        if (cubicacion is not null && cubicacion.MotivoSinSeleccion is not null
            && !string.Equals(cubicacion.Resultado, "SIN_SELECCION_AUTOMATICA", StringComparison.Ordinal))
        {
            throw new ArgumentException("El motivo de 'sin selección' solo existe con SIN_SELECCION_AUTOMATICA.", nameof(cubicacion));
        }

        IdRecomendacion = cubicacion?.IdRecomendacion;
        ResultadoCubicacion = cubicacion?.Resultado;
        MotivoSinSeleccion = cubicacion?.MotivoSinSeleccion;
        Condicionada = cubicacion?.Condicionada ?? false;
        AvisosCubicacion = cubicacion?.Avisos;
    }

    /// <summary>Código de la advertencia que deja el motivo real cuando la base no lo admite (<see cref="DegradarMotivoSinSeleccion"/>).</summary>
    public const string CodigoAvisoMotivoNoGuardado = "MOTIVO_SIN_SELECCION";

    /// <summary>
    /// HU-023 (#176), respaldo de la opción A: la base todavía no admite el motivo (falta <c>db/scripts/07</c>, que agrega
    /// <c>BUSQUEDA_AGOTADA</c> al CHECK). El motivo se guarda en <c>NULL</c> y el valor real queda como advertencia visible de la ruta
    /// (<see cref="CodigoAvisoMotivoNoGuardado"/>), con el <paramref name="mensaje"/> para el comercial. No hace nada sin motivo.
    /// </summary>
    public void DegradarMotivoSinSeleccion(string mensaje)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mensaje);
        if (MotivoSinSeleccion is not { } motivo)
        {
            return;
        }

        var avisos = AvisosCubicacion ?? new clsAvisosCubicacion([], [], null);
        var advertencias = avisos.Advertencias ?? [];
        if (!advertencias.Any(a => a.Codigo == CodigoAvisoMotivoNoGuardado))
        {
            advertencias = [.. advertencias, new clsAdvertenciaCubicador(CodigoAvisoMotivoNoGuardado, $"{motivo}: {mensaje}")];
        }

        AvisosCubicacion = avisos with { Advertencias = advertencias };
        MotivoSinSeleccion = null;
    }

    /// <summary>
    /// HU-023.3: reemplaza los camiones de la ruta por un plan nuevo del cubicador (recalcular la ruta, re-solicitud con cambios de carga),
    /// con su resultado. Solo antes de enviar a Nexo: los camiones anteriores se quitan y los nuevos nacen sin estado ni costo.
    /// </summary>
    public void ReemplazarCamiones(IReadOnlyList<clsCamionPlaneado> camiones, clsCubicacionRuta? cubicacion)
    {
        ArgumentNullException.ThrowIfNull(camiones);
        if (camiones.Count == 0)
        {
            throw new ArgumentException("La ruta debe tener al menos un camión.", nameof(camiones));
        }

        _vehiculos.Clear();
        foreach (var camion in camiones)
        {
            AgregarVehiculo(camion);
        }

        RegistrarCubicacion(cubicacion);
    }

    /// <summary>
    /// HU-023.5 (#160): la ruta queda esperando la cubicación en segundo plano (con su camión provisional sin vehículo). Solo al crearla.
    /// </summary>
    internal void MarcarCubicacionPendiente()
    {
        EstadoCubicacion = Cotizaciones.EstadoCubicacion.Pendiente;
        CubicacionIntentos = 0;
        CubicacionDisponibleAt = null;
        ErrorCubicacion = null;
    }

    /// <summary>HU-023.5: la cubicación en segundo plano terminó: los camiones del plan reemplazan al provisional.</summary>
    internal void CompletarCubicacion(clsPlanRuta plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ReemplazarCamiones(plan.Camiones, plan.Cubicacion);
        EstadoCubicacion = Cotizaciones.EstadoCubicacion.Cubicada;
        CubicacionDisponibleAt = null;
        ErrorCubicacion = null;
    }

    /// <summary>
    /// HU-023.5: un intento de cubicar la ruta falló. Con <paramref name="reintentarEn"/> la ruta sigue pendiente y se vuelve a tomar
    /// desde esa hora; sin él queda <see cref="Cotizaciones.EstadoCubicacion.Fallida"/> (con el error visible para el comercial).
    /// </summary>
    internal void RegistrarFalloCubicacion(string codigo, string mensaje, DateTimeOffset? reintentarEn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codigo);
        CubicacionIntentos++;
        var error = $"{codigo.Trim()}: {mensaje?.Trim()}";
        ErrorCubicacion = error.Length > LargoMaximoErrorCubicacion ? error[..LargoMaximoErrorCubicacion] : error;
        EstadoCubicacion = reintentarEn is null ? Cotizaciones.EstadoCubicacion.Fallida : Cotizaciones.EstadoCubicacion.Pendiente;
        CubicacionDisponibleAt = reintentarEn;
    }

    /// <summary>
    /// Opción C (validación de los defectos del 2026-10-16): el cubicador estaba ocupado (<c>CUBICADOR_OCUPADO</c>, no se le llamó). La
    /// ruta sigue pendiente desde <paramref name="disponibleAt"/>, sin gastar un intento ni anotar un error.
    /// </summary>
    internal void PosponerCubicacion(DateTimeOffset disponibleAt) => CubicacionDisponibleAt = disponibleAt;

    /// <summary>
    /// HU-023.5: ¿la ruta falló porque el cubicador no respondió (caída o tiempo agotado, <c>CUBICADOR_NO_DISPONIBLE</c>)? Solo esas se
    /// pueden aceptar con el camión sin vehículo: un rechazo de la petición (mercancía desconocida, dimensiones inválidas...) o una
    /// respuesta inválida es un dato malo que el respaldo escondería (HU-023.1, contra el fallo silencioso).
    /// </summary>
    public bool FalloPorCaidaDelCubicador =>
        EstadoCubicacion == Cotizaciones.EstadoCubicacion.Fallida
        && ErrorCubicacion is { } error
        && error.StartsWith(clsCodigosError.CubicadorNoDisponible + ":", StringComparison.Ordinal);

    /// <summary>
    /// HU-023.5: el comercial reintenta una ruta fallida. Sin <paramref name="usarRespaldo"/> vuelve a la cola. Con él, solo si falló por
    /// una caída del cubicador (<see cref="FalloPorCaidaDelCubicador"/>), acepta el camión provisional sin vehículo (lo resuelve la Mesa
    /// de Costeo de Nexo) y la ruta queda cubicada sin resultado ni error; un rechazo no cambia (devuelve <c>false</c>).
    /// </summary>
    internal bool ReintentarCubicacion(bool usarRespaldo)
    {
        if (EstadoCubicacion != Cotizaciones.EstadoCubicacion.Fallida || (usarRespaldo && !FalloPorCaidaDelCubicador))
        {
            return false;
        }

        CubicacionIntentos = 0;
        CubicacionDisponibleAt = null;
        if (usarRespaldo)
        {
            EstadoCubicacion = Cotizaciones.EstadoCubicacion.Cubicada;
            ErrorCubicacion = null;
            RegistrarCubicacion(null);
            return true;
        }

        EstadoCubicacion = Cotizaciones.EstadoCubicacion.Pendiente;
        ErrorCubicacion = null;
        return true;
    }

    /// <summary>
    /// La ruta como línea de la captura (para revalidarla o aplicarle cambios de carga): con el vehículo de su único camión (o vacío si
    /// tiene varios) y sus mercancías en orden. Es la adaptación "una ruta, un camión" de HU-021 (ADR-019).
    /// </summary>
    public clsLineaSolicitudCosteo ALinea()
    {
        var unico = _vehiculos.Count == 1 ? _vehiculos[0] : null;
        return new clsLineaSolicitudCosteo
        {
            Origen = OrigenNombre ?? string.Empty,
            Destino = DestinoNombre ?? string.Empty,
            OrigenMunicipioId = OrigenMunicipioId,
            DestinoMunicipioId = DestinoMunicipioId,
            TipoVehiculo = unico?.TipoVehiculo,
            Carroceria = unico?.Carroceria,
            Tipologia = Tipologia,
            ClaseViaje = ClaseViaje,
            ClaseContenedor = ClaseContenedor,
            Exclusivo = Exclusivo,
            Consolidado = Consolidado,
            RetornaContenedor = RetornaContenedor,
            CiudadDevolucionContenedor = CiudadDevolucionNombre,
            CiudadDevolucionMunicipioId = CiudadDevolucionMunicipioId,
            LevanteContenedorDiferente = LevanteContenedorDiferente,
            CiudadLevanteContenedor = CiudadLevanteNombre,
            CiudadLevanteMunicipioId = CiudadLevanteMunicipioId,
            ValorAprovisionamiento = ValorLevanteContenedor,
            PesoKg = PesoKg,
            ValorMercancia = ValorMercancia,
            PesoConTaraKg = PesoConTaraKg,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Posiciones = [.. MercanciasOrdenadas.Select(m => m.APosicion())],
        };
    }

    /// <summary>
    /// Línea que viaja a Nexo por <paramref name="camion"/> (una línea por camión, <c>CAMBIOS_BACKEND</c> §5.3): los datos de la ruta
    /// (origen y destino por nombre e id, condiciones, horas, valor de la mercancía) y los del camión (vehículo, carrocería, peso y su
    /// levante como <c>valorAprovisionamiento</c>) y el id de la ruta (<see cref="clsLineaSolicitudCosteo.RutaId"/>, para N1 de Nexo). Las
    /// mercancías viajan como posiciones solo si la ruta tiene un único camión: con varios, a Nexo le basta el peso de cada uno (el
    /// reparto lo sabe el cubicador). El peso es <see cref="PesoLineaNexo"/> y el camión del cubicador viaja en
    /// <see cref="clsLineaSolicitudCosteo.IdRecomendacionVehiculo"/> (HU-023.4, #159). HU-030.1 (#184): una ruta de contenedor con
    /// <see cref="PesoConTaraKg"/> manda el peso con tara y NUNCA posiciones, tampoco con un solo camión: Nexo valida que el peso de una
    /// línea con posiciones cuadre al 1 % con su suma (<c>PESO_INCONSISTENTE</c>, confirmado al cerrar NEXO#308) y la tara lo
    /// descuadraría. La línea no lleva <see cref="clsLineaSolicitudCosteo.PesoConTaraKg"/>: su <c>PesoKg</c> ya es el peso con tara.
    /// </summary>
    public clsLineaSolicitudCosteo ALineaNexo(clsVehiculoLineaSolicitud camion)
    {
        ArgumentNullException.ThrowIfNull(camion);
        if (!_vehiculos.Contains(camion))
        {
            throw new ArgumentException("El camión no es de esta ruta.", nameof(camion));
        }

        var linea = ALinea();
        return linea with
        {
            RutaId = Id,
            IdRecomendacionVehiculo = camion.IdRecomendacionVehiculo,
            TipoVehiculo = camion.TipoVehiculo,
            Carroceria = camion.Carroceria,
            PesoKg = PesoLineaNexo(camion),
            PesoConTaraKg = null,
            ValorAprovisionamiento = ValorLevanteContenedor is null ? null : camion.CostoLevanteContenedor,
            Posiciones = UnSoloCamion && !LlevaTara ? linea.Posiciones : [],
        };
    }

    /// <summary>
    /// HU-030.1 (#184): ¿la ruta manda a Nexo el peso con tara? Solo una ruta <c>Contenedor</c> con <see cref="PesoConTaraKg"/> (la
    /// captura no la acepta en otra clase; si una ruta guardada cambiara de clase, la tara deja de aplicarse en lugar de viajar).
    /// </summary>
    public bool LlevaTara => PesoConTaraKg is not null
        && string.Equals(ClaseViaje, clsCatalogosCaptura.ClaseViajeContenedor, StringComparison.Ordinal);

    /// <summary>
    /// HU-030.1 (#184): el peso con tara que viaja a Nexo en la línea de <paramref name="camion"/> (<see cref="PesoLineaNexo"/>), o
    /// <c>null</c> si la ruta no lleva tara. Es lo que el comercial ve por camión en el costeo.
    /// </summary>
    public decimal? PesoConTaraCamion(clsVehiculoLineaSolicitud camion) => LlevaTara ? PesoLineaNexo(camion) : null;

    /// <summary>
    /// ¿La ruta tiene un solo camión (contando los descartados)? Decide si la línea lleva las posiciones y el peso de la ruta
    /// (<see cref="PesoLineaNexo"/>). Cuenta TODOS los camiones, no solo los vigentes: un camión que la Mesa descarta después del envío no
    /// cambia lo que ya viajó, así que el emparejador del polling vuelve a calcular exactamente el peso enviado (HU-023.4, validación).
    /// </summary>
    private bool UnSoloCamion => _vehiculos.Count == 1;

    /// <summary>
    /// Peso que viaja a Nexo en la línea de <paramref name="camion"/> (HU-023.4, #159, decisión 6 del 2026-10-13). Con <b>un solo camión</b>
    /// la línea lleva las posiciones, así que lleva el <b>peso declarado de la ruta</b> (o, sin él, la suma de las posiciones): ese ya cuadra
    /// al 1 % con las posiciones y Nexo no responde <c>PESO_INCONSISTENTE</c>. El peso del cubicador (<see cref="clsVehiculoLineaSolicitud.PesoKg"/>)
    /// se guarda y se muestra, pero no viaja. Con <b>varios camiones</b> (sin posiciones) va el peso de cada camión, como antes. El emparejador
    /// compara con este mismo peso, en una sola pasada (<see cref="clsCamionParaEmparejar.Desde"/>).
    /// </summary>
    /// <remarks>
    /// HU-030.1 (#184): en una ruta que <see cref="LlevaTara"/> (contenedor con <see cref="PesoConTaraKg"/>) la tara total (peso con tara
    /// menos el peso de carga de la ruta) se reparte <b>por partes iguales</b> entre TODOS sus camiones (uno por contenedor, de la misma
    /// clase: la misma tara), en céntimos, y el último camión en orden (<see cref="VehiculosOrdenados"/>) se queda con el resto del
    /// redondeo: la suma de las líneas es exactamente el peso con tara. Con un solo camión, la línea lleva el peso con tara tal cual. Como
    /// el peso sin tara, cuenta los camiones descartados: lo que ya viajó no cambia y el emparejador vuelve a calcular el mismo peso.
    /// </remarks>
    public decimal? PesoLineaNexo(clsVehiculoLineaSolicitud camion)
    {
        ArgumentNullException.ThrowIfNull(camion);
        var sinTara = PesoCargaLineaNexo(camion);
        return sinTara is { } carga && TaraDelCamion(camion) is { } tara ? carga + tara : sinTara;
    }

    /// <summary>El peso de la línea de <paramref name="camion"/> SIN tara (HU-023.4, decisión 6): el de la ruta con un camión; si no, el del camión.</summary>
    private decimal? PesoCargaLineaNexo(clsVehiculoLineaSolicitud camion)
    {
        if (!UnSoloCamion)
        {
            return camion.PesoKg;
        }

        var suma = SumaPesoMercancias;
        return PesoKg ?? (suma > 0 ? suma : camion.PesoKg);
    }

    /// <summary>Suma <c>cantidad × pesoKg</c> de las mercancías con peso (la misma de Nexo).</summary>
    private decimal SumaPesoMercancias =>
        MercanciasOrdenadas.Select(m => m.APosicion()).Where(p => p.PesoKg is > 0).Sum(p => p.Cantidad * p.PesoKg!.Value);

    /// <summary>
    /// HU-030.1 (#184): la parte de la tara que lleva <paramref name="camion"/> (ver <see cref="PesoLineaNexo"/>), o <c>null</c> si la
    /// ruta no lleva tara o no se conoce su peso de carga. Una tara negativa (dato anterior a la validación) cuenta como 0.
    /// </summary>
    private decimal? TaraDelCamion(clsVehiculoLineaSolicitud camion)
    {
        var suma = SumaPesoMercancias;
        if (!LlevaTara || (PesoKg ?? (suma > 0 ? suma : null)) is not { } carga)
        {
            return null;
        }

        var tara = Math.Max(0m, PesoConTaraKg!.Value - carga);
        var camiones = VehiculosOrdenados;
        var parte = Math.Round(tara / camiones.Count, clsSolicitudCosteo.Decimales, MidpointRounding.ToZero);
        return ReferenceEquals(camiones[^1], camion) ? tara - (parte * (camiones.Count - 1)) : parte;
    }

    /// <summary>
    /// Completa los ids de municipio que faltan (HU-013.11): una ruta guardada sin ellos se reenvía con los ids resueltos, y se guardan
    /// para que el siguiente reenvío mande lo mismo. Un id que ya tiene no se cambia. Devuelve <c>true</c> si cambió.
    /// </summary>
    public bool CompletarMunicipios(int? origenMunicipioId, int? destinoMunicipioId)
    {
        if (origenMunicipioId is <= 0 || destinoMunicipioId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(origenMunicipioId), "Un id de municipio de Nexo es un entero positivo.");
        }

        var antes = (OrigenMunicipioId, DestinoMunicipioId);
        OrigenMunicipioId ??= origenMunicipioId;
        DestinoMunicipioId ??= destinoMunicipioId;
        return antes != (OrigenMunicipioId, DestinoMunicipioId);
    }

    // ------------------------------------------------------------------ Estado del costeo

    /// <summary>
    /// Un estado de toda la solicitud (envío, error de envío, entrega...) llega a todos los camiones de la ruta y la ruta lo resume.
    /// Devuelve <c>true</c> si algo cambió.
    /// </summary>
    internal bool RegistrarEstadoCamiones(EstadoCosteo estado)
    {
        // HU-021.9: un camión descartado en Nexo sigue descartado (no viaja en un reenvío); si lo están todos, cuentan todos.
        var cambio = false;
        foreach (var camion in VehiculosQueCuentan)
        {
            cambio |= camion.RegistrarEstado(estado);
        }

        return ResumirEstado() | cambio;
    }

    /// <summary>
    /// Vence los camiones sin flete (<c>pendiente_costeo</c> o sin estado): el polling llegó a la edad máxima o Nexo ya no reconoce la
    /// solicitud. Los que tienen flete conservan su estado y su costo. Devuelve <c>true</c> si algo cambió.
    /// </summary>
    internal bool VencerCamionesSinFlete()
    {
        var cambio = false;
        foreach (var camion in _vehiculos.Where(c => c.EstadoCosteo is Cotizaciones.EstadoCosteo.PendienteCosteo or Cotizaciones.EstadoCosteo.PendienteEnvio or null))
        {
            cambio |= camion.RegistrarEstado(Cotizaciones.EstadoCosteo.CosteoVencido);
        }

        return ResumirEstado() | cambio;
    }

    /// <summary>
    /// Recalcula <see cref="EstadoCosteo"/> como el resumen de sus camiones (§9.2); <paramref name="solicitudEnviada"/> decide cómo cuenta un
    /// camión sin estado (<see cref="clsResumenEstadoCosteo"/>). Devuelve <c>true</c> si cambió.
    /// </summary>
    internal bool ResumirEstado(bool solicitudEnviada = false)
    {
        var resumen = clsResumenEstadoCosteo.Resumir(_vehiculos.Select(v => v.EstadoCosteo), solicitudEnviada);
        var cambio = EstadoCosteo != resumen;
        EstadoCosteo = resumen;
        return cambio;
    }

    /// <summary>
    /// Re-solicitud en el mismo folio (HU-013.4): sus camiones vuelven a viajar a Nexo como líneas nuevas. Se olvidan los costos y el
    /// precio de Nexo (los conexos y el levante, de Cotifly, se conservan) y los camiones quedan en <c>pendiente_envio</c>. HU-021.9: los
    /// camiones <c>descartada</c> en Nexo (la Mesa borró su línea) siguen descartados y no se reenvían, igual que en la <c>-vN</c>; si lo
    /// están todos, se reenvían todos (<see cref="VehiculosQueCuentan"/>).
    /// </summary>
    internal void ReiniciarCosteo()
    {
        foreach (var camion in VehiculosQueCuentan)
        {
            camion.ReiniciarCosteo();
            camion.RegistrarEstado(Cotizaciones.EstadoCosteo.PendienteEnvio);
        }

        SumarCostos();
        PrecioFinalLinea = null;
        ResumirEstado();
    }

    // ------------------------------------------------------------------ Totales y precio (§9.1)

    /// <summary>
    /// Recalcula y guarda los totales de costo y el precio de la ruta (§9.1): se llama cada vez que cambia el costo de un camión, un
    /// conexo o el margen. El precio es <see cref="CostoFinal"/> con <see cref="MargenAplicado"/> según <see cref="MetodoCalculo"/>, en
    /// pesos o convertido a la <paramref name="moneda"/> con la <paramref name="trm"/> fijada en la solicitud (ADR-014). Sin costo final o
    /// sin margen el precio queda vacío. Devuelve si algo cambió, o el error del módulo de precio (la ruta queda sin precio).
    /// </summary>
    public clsResultado<bool> RecalcularTotales(string? moneda, decimal trm)
    {
        var antes = Huella();
        SumarCostos();
        var error = PrecioDe(CostoFinal, moneda, trm, out var precio);
        PrecioFinalLinea = precio;
        if (error is not null)
        {
            return error;
        }

        return antes != Huella();
    }

    /// <summary>
    /// Precio de <paramref name="costo"/> con el margen y el método de la ruta, en la moneda de la solicitud (la misma fórmula del
    /// precio de la ruta; la usan también el PDF y la vista previa para el flete y los conexos). <paramref name="precio"/> queda en
    /// <c>null</c> sin costo, sin margen o con error; devuelve el error del módulo de precio, o <c>null</c>.
    /// </summary>
    public clsErrorDominio? PrecioDe(decimal? costo, string? moneda, decimal trm, out decimal? precio) =>
        PrecioCon(costo, MargenAplicado, MetodoCalculo, moneda, trm, out precio);

    /// <summary>Precio de <paramref name="costo"/> con un margen dado (vista previa del ajuste) en la moneda de la solicitud.</summary>
    public static clsErrorDominio? PrecioCon(decimal? costo, decimal? margen, string? metodo, string? moneda, decimal trm, out decimal? precio)
    {
        precio = null;
        if (costo is not { } c || margen is not { } m || string.IsNullOrWhiteSpace(metodo))
        {
            return null;
        }

        var enPesos = clsCalculadoraPrecio.Calcular(c, m, metodo);
        if (enPesos.EsFallo)
        {
            return enPesos.Error;
        }

        if (clsConversorMoneda.EsPesos(moneda))
        {
            precio = enPesos.Valor.Precio;
            return null;
        }

        var convertido = clsConversorMoneda.PesosADolares(enPesos.Valor.Precio, trm);
        if (convertido.EsFallo)
        {
            return convertido.Error;
        }

        precio = convertido.Valor;
        return null;
    }

    /// <summary>Costo del transporte (fletes + retorno + levante, sin conexos), o <c>null</c> mientras algún camión no tenga flete.</summary>
    public decimal? CostoTransporte =>
        CostoFletesTotal is { } fletes && CostoRetornoContenedorTotal is { } retorno ? fletes + retorno + (CostoLevanteContenedorTotal ?? 0m) : null;

    /// <summary>
    /// Camiones que cuentan para el estado y los totales: los <c>descartada</c> en Nexo se ignoran, salvo que todos lo estén (entonces
    /// cuentan todos y la ruta queda descartada y sin flete). Regla del coordinador del 2026-10-08 (ADR-019).
    /// </summary>
    public IReadOnlyList<clsVehiculoLineaSolicitud> VehiculosQueCuentan
    {
        get
        {
            var vigentes = _vehiculos.Where(v => v.EstadoCosteo != Cotizaciones.EstadoCosteo.Descartada).ToList();
            return vigentes.Count > 0 ? vigentes : [.. _vehiculos];
        }
    }

    /// <summary>¿Todos sus camiones que cuentan (<see cref="VehiculosQueCuentan"/>) tienen flete? (sin camiones, no.)</summary>
    public bool TodosConFlete => VehiculosQueCuentan is { Count: > 0 } cuentan && cuentan.All(v => v.CostoFlete is not null);

    private void SumarCostos()
    {
        var cuentan = VehiculosQueCuentan;
        var conFlete = TodosConFlete;
        CostoFletesTotal = conFlete ? cuentan.Sum(v => v.CostoFlete!.Value) : null;
        CostoRetornoContenedorTotal = conFlete ? cuentan.Sum(v => v.CostoRetornoContenedor ?? 0m) : null;
        CostoLevanteContenedorTotal = cuentan.Count == 0 ? null : cuentan.Sum(v => v.CostoLevanteContenedor ?? 0m);
        CostoServiciosConexos = _conexos.Sum(s => s.Costo);
        CostoFinal = conFlete ? CostoFletesTotal + CostoRetornoContenedorTotal + (CostoLevanteContenedorTotal ?? 0m) + CostoServiciosConexos : null;
    }

    private (decimal?, decimal?, decimal?, decimal?, decimal?, decimal?) Huella() =>
        (CostoFletesTotal, CostoRetornoContenedorTotal, CostoLevanteContenedorTotal, CostoServiciosConexos, CostoFinal, PrecioFinalLinea);

    /// <summary>
    /// Precio de la ruta tal como quedó congelado (PDF ya generado, o el chat): no se recalcula. Lo usa el chat, que trae el precio
    /// calculado y verificado por el módulo de Precio.
    /// </summary>
    public bool FijarPrecio(decimal? precioFinalLinea)
    {
        if (precioFinalLinea is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(precioFinalLinea), "El precio no puede ser negativo.");
        }

        var cambio = PrecioFinalLinea != precioFinalLinea;
        PrecioFinalLinea = precioFinalLinea;
        return cambio;
    }

    // ------------------------------------------------------------------ Conexos

    /// <summary>
    /// Agrega el conexo o, si ya está (mismo <paramref name="idServicioConexo"/>), actualiza su precio unitario, cantidad y nombre. No
    /// recalcula: es interno; desde fuera se usa <see cref="clsCotizacionSolicitud.CambiarConexos"/>, que siempre recalcula. Devuelve
    /// <c>true</c> si cambió algo.
    /// </summary>
    internal bool AgregarOActualizarConexo(string idServicioConexo, string? nombre, decimal precio, decimal cantidad) =>
        AgregarOActualizarConexo(new clsCambioConexo(idServicioConexo, nombre, precio, cantidad));

    /// <summary>
    /// <see cref="AgregarOActualizarConexo(string, string?, decimal, decimal)"/> con el id del catálogo de Nexo y la categoría del conexo
    /// (HU-014, #188, scripts 03 y 05), si se conocen: se guardan en un conexo nuevo y completan los de una fila que no los tenía.
    /// </summary>
    internal bool AgregarOActualizarConexo(clsCambioConexo conexo)
    {
        ArgumentNullException.ThrowIfNull(conexo);
        ArgumentException.ThrowIfNullOrWhiteSpace(conexo.IdServicioConexo);
        var id = conexo.IdServicioConexo.Trim();
        if (_conexos.FirstOrDefault(c => string.Equals(c.IdServicioConexo, id, StringComparison.OrdinalIgnoreCase)) is { } existente)
        {
            return existente.Actualizar(conexo.Nombre, conexo.Precio, conexo.Cantidad, conexo.IdServicioConexoCatalogo, conexo.Categoria);
        }

        _conexos.Add(clsServicioConexoRuta.Crear(
            Id, id, conexo.Nombre, conexo.Precio, conexo.Cantidad, conexo.IdServicioConexoCatalogo, conexo.Categoria));
        return true;
    }

    /// <summary>
    /// Quita los conexos que no están en <paramref name="conservar"/>. Devuelve los quitados. Interno: desde fuera se usa
    /// <see cref="clsCotizacionSolicitud.ConservarConexos"/>, que siempre recalcula.
    /// </summary>
    internal IReadOnlyList<clsServicioConexoRuta> ConservarConexos(IReadOnlySet<string> conservar)
    {
        ArgumentNullException.ThrowIfNull(conservar);
        var quitados = _conexos.Where(c => !conservar.Contains(c.IdServicioConexo)).ToList();
        foreach (var conexo in quitados)
        {
            _conexos.Remove(conexo);
        }

        return quitados;
    }

    /// <summary>
    /// Deja exactamente los conexos de <paramref name="cantidades"/> (id → cantidad nueva): quita los demás y cambia la cantidad de los
    /// que siguen (mismo precio unitario). No recalcula: desde fuera se usa <see cref="clsCotizacionSolicitud.AjustarConexosRuta"/>.
    /// Un id que no es conexo de la ruta lanza (el caso de uso lo valida antes). Devuelve los movimientos, en el orden de los conexos.
    /// </summary>
    internal IReadOnlyList<clsMovimientoConexo> AjustarConexos(IReadOnlyDictionary<string, decimal> cantidades)
    {
        ArgumentNullException.ThrowIfNull(cantidades);
        if (cantidades.Keys.FirstOrDefault(id => _conexos.All(c => !string.Equals(c.IdServicioConexo, id, StringComparison.OrdinalIgnoreCase))) is { } ajeno)
        {
            throw new ArgumentException($"El conexo {ajeno} no es de la ruta {Orden}.", nameof(cantidades));
        }

        var movimientos = new List<clsMovimientoConexo>();
        foreach (var conexo in _conexos.OrderBy(c => c.CreatedAt).ThenBy(c => c.IdServicioConexo, StringComparer.Ordinal).ToList())
        {
            var cantidad = cantidades.FirstOrDefault(p => string.Equals(p.Key, conexo.IdServicioConexo, StringComparison.OrdinalIgnoreCase));
            if (cantidad.Key is null)
            {
                _conexos.Remove(conexo);
                movimientos.Add(new clsMovimientoConexo(conexo.IdServicioConexo, conexo.Nombre, conexo.Cantidad, null));
                continue;
            }

            var anterior = conexo.Cantidad;
            if (conexo.Actualizar(null, conexo.Precio, cantidad.Value))
            {
                movimientos.Add(new clsMovimientoConexo(conexo.IdServicioConexo, conexo.Nombre, anterior, cantidad.Value));
            }
        }

        return movimientos;
    }

    /// <summary>
    /// Cambia el valor de UN levante de la ruta y lo copia a cada camión (decisión 4). No recalcula: desde fuera se usa
    /// <see cref="clsCotizacionSolicitud.CambiarValorLevante"/>. Devuelve <c>true</c> si cambió.
    /// </summary>
    internal bool CambiarValorLevante(decimal? valor)
    {
        if (valor is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(valor), "El valor del levante no puede ser negativo.");
        }

        if (ValorLevanteContenedor == valor)
        {
            return false;
        }

        ValorLevanteContenedor = valor;
        foreach (var camion in _vehiculos)
        {
            camion.CopiarLevante(valor);
        }

        return true;
    }

    // ------------------------------------------------------------------ Margen (ex clsCotizacion, HU-013.2, ADR-018 §3)

    /// <summary>Margen máximo del ajuste del comercial sobre el costeo (ADR-018 §3): 0,99.</summary>
    public const decimal MargenMaximoAjuste = 0.99m;

    /// <summary>Largo máximo de la justificación de un ajuste de margen (contrato <c>AjusteMargenWeb</c>).</summary>
    public const int LargoMaximoJustificacion = 500;

    /// <summary>
    /// Margen sugerido vigente para la regla de la justificación: el de SAP TM si aplicó, si no el de la política de su línea; sin
    /// ninguno (filas viejas), el aplicado si nadie lo ajustó.
    /// </summary>
    public decimal? SugeridoVigente => MargenSapValido ?? MargenPoliticaSugerido
        ?? (OrigenMargen == Margen.OrigenMargen.Negociado ? null : MargenAplicado);

    /// <summary>
    /// HU-009 (#139, B1): ¿el margen que propuso SAP TM para la ruta está fuera de [0, 60 %] (<see cref="clsReglasMargen.EsAnomalo"/>)? Se
    /// guarda en <see cref="MargenSAPSugerido"/> para que quede visible, pero no se aplica ni cuenta como sugerido: la ruta usa el de la
    /// política de su línea (<see cref="RegistrarMargenSapAnomalo"/>).
    /// </summary>
    public bool MargenSapAnomalo => MargenSAPSugerido is { } sap && clsReglasMargen.EsAnomalo(sap);

    /// <summary>Sugerido que se muestra al comercial: el de SAP TM si no es anómalo, si no el de la política de la línea.</summary>
    public decimal? MargenSugeridoMostrado => MargenSapValido ?? MargenPoliticaSugerido;

    private decimal? MargenSapValido => MargenSapAnomalo ? null : MargenSAPSugerido;

    /// <summary>Fuente del sugerido vigente: <c>sap_tm</c> o <c>politica_linea</c> (nunca <c>negociado</c>).</summary>
    public OrigenMargen? OrigenMargenSugerido => MargenSapValido is not null ? Margen.OrigenMargen.SapTm
        : MargenPoliticaSugerido is not null ? Margen.OrigenMargen.PoliticaLinea
        : OrigenMargen is Margen.OrigenMargen.SapTm or Margen.OrigenMargen.PoliticaLinea && MargenAplicado is not null ? OrigenMargen
        : null;

    /// <summary>¿El comercial ajustó el margen? (<c>origen_margen = negociado</c> con margen y método.)</summary>
    public bool TieneMargenNegociado =>
        OrigenMargen == Margen.OrigenMargen.Negociado && MargenAplicado is not null && MetodoCalculo is not null;

    /// <summary>¿La ruta tiene precio con margen? (precio y método.)</summary>
    public bool TienePrecio => PrecioFinalLinea is not null && MetodoCalculo is not null && MargenAplicado is not null;

    /// <summary>
    /// Margen resuelto al enviar la solicitud a Nexo (HU-005.7, ADR-015 §5): SAP TM con el BP o la política de su línea de negocio. Se
    /// guarda para aplicarlo cuando llegue el costeo. Valida el margen con su método (<see cref="clsReglaMargen"/>).
    /// </summary>
    public void RegistrarMargenCosteo(decimal? margenPolitica, decimal margenAplicado, OrigenMargen origen, string metodoCalculo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metodoCalculo);
        var metodo = metodoCalculo.Trim().ToLowerInvariant();
        if (clsValidacion.Primero(
                clsReglaMargen.Validar(clsCotizacionSolicitud.CodigoError, "margenPolitica", margenPolitica, metodo),
                clsReglaMargen.Validar(clsCotizacionSolicitud.CodigoError, "margenAplicado", margenAplicado, metodo)) is { } error)
        {
            throw new ArgumentException(error.Mensaje, nameof(margenAplicado));
        }

        FijarSugeridos(margenPolitica, margenAplicado, origen);
        MargenAplicado = margenAplicado;
        OrigenMargen = origen;
        MetodoCalculo = metodo;
    }

    /// <summary>
    /// HU-009 (#139, B1): SAP TM propuso para la ruta un margen anómalo (fuera de [0, 60 %]). Se llama DESPUÉS de registrar el margen de la
    /// política de la línea (<see cref="RegistrarMargenCosteo"/> o <see cref="RefrescarMargenSugerido"/>): deja el valor de SAP en
    /// <see cref="MargenSAPSugerido"/> como marca visible (<see cref="MargenSapAnomalo"/>) sin aplicarlo ni usarlo como sugerido.
    /// </summary>
    public void RegistrarMargenSapAnomalo(decimal margenSap)
    {
        if (!clsReglasMargen.EsAnomalo(margenSap))
        {
            throw new ArgumentOutOfRangeException(nameof(margenSap), margenSap, "Solo un margen de SAP TM fuera de [0, 0,6] es anómalo.");
        }

        MargenSAPSugerido = margenSap;
    }

    /// <summary>
    /// Re-solicitud con margen negociado (HU-013.4, decisión del 2026-10-06): conserva el margen aplicado y el origen <c>negociado</c>, y
    /// refresca los sugeridos (SAP TM o la política de la línea) como referencia.
    /// </summary>
    public void RefrescarMargenSugerido(decimal? margenPolitica, decimal sugerido, OrigenMargen origen)
    {
        if (sugerido < 0m || origen == Margen.OrigenMargen.Negociado)
        {
            throw new ArgumentOutOfRangeException(nameof(sugerido), "El sugerido es no negativo y sale de SAP TM o de la política.");
        }

        MargenPoliticaSugerido = margenPolitica ?? (origen == Margen.OrigenMargen.PoliticaLinea ? sugerido : MargenPoliticaSugerido);
        MargenSAPSugerido = origen == Margen.OrigenMargen.SapTm ? sugerido : null;
    }

    /// <summary>
    /// Invariantes del ajuste de margen sobre el costeo de la ruta (ADR-018 §3), en este orden: rango y largo de la justificación
    /// (<c>AJUSTE_MARGEN_INVALIDO</c>), costo con margen (<c>COSTEO_NO_LISTO</c>) y justificación si queda por debajo del sugerido
    /// (<c>JUSTIFICACION_REQUERIDA</c>). La regla del PDF es de la solicitud (<see cref="clsCotizacionSolicitud.ValidarAjusteMargen"/>).
    /// </summary>
    public clsErrorDominio? ValidarAjusteMargen(decimal margen, string? justificacion, string folio)
    {
        if (ValidarForma(margen, justificacion) is { } forma)
        {
            return forma;
        }

        if (!TienePrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La ruta {Orden} de la cotización {folio} todavía no tiene el costeo de Nexo con margen aplicado.");
        }

        return ValidarJustificacion(margen, justificacion);
    }

    /// <summary>Rango del margen (0 a <see cref="MargenMaximoAjuste"/>) y largo de la justificación (<c>AJUSTE_MARGEN_INVALIDO</c>).</summary>
    public static clsErrorDominio? ValidarForma(decimal margen, string? justificacion)
    {
        if (margen < 0m || margen > MargenMaximoAjuste)
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido,
                $"El margen debe estar entre 0 y {MargenMaximoAjuste.ToString(System.Globalization.CultureInfo.InvariantCulture)} (fracción: 0.18 = 18 %).");
        }

        return justificacion is { Length: > LargoMaximoJustificacion }
            ? new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, $"La justificación admite hasta {LargoMaximoJustificacion} caracteres.")
            : null;
    }

    /// <summary><c>JUSTIFICACION_REQUERIDA</c> si el margen queda por debajo del sugerido vigente sin justificación.</summary>
    public clsErrorDominio? ValidarJustificacion(decimal margen, string? justificacion)
    {
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        return SugeridoVigente is { } sugerido && redondeado < sugerido && clsMargenAjuste.LimpiarJustificacion(justificacion) is null
            ? new clsErrorDominio(clsCodigosError.JustificacionRequerida,
                $"El margen {clsReglasMargen.Texto(redondeado)} queda por debajo del sugerido ({clsReglasMargen.Texto(sugerido)}): escribe la justificación.")
            : null;
    }

    /// <summary>
    /// Aplica el margen negociado (ya validado, redondeado a 4 decimales) y el origen pasa a <c>negociado</c>. No toca los sugeridos
    /// (en una fila sin sugerido lo fija con el vigente, para no perderlo). No recalcula el precio (<see cref="RecalcularTotales"/>).
    /// Devuelve <c>false</c> (sin cambios) si es el margen vigente.
    /// </summary>
    public bool AplicarMargenNegociado(decimal margen)
    {
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        if (redondeado < 0m || redondeado > MargenMaximoAjuste)
        {
            throw new ArgumentOutOfRangeException(nameof(margen), margen, "El margen negociado debe estar entre 0 y 0,99.");
        }

        if (MargenAplicado == redondeado)
        {
            return false;
        }

        if (MargenSAPSugerido is null && MargenPoliticaSugerido is null && SugeridoVigente is { } vigente)
        {
            if (OrigenMargen is Margen.OrigenMargen.SapTm)
            {
                MargenSAPSugerido = vigente;
            }
            else
            {
                MargenPoliticaSugerido = vigente;
            }
        }

        MargenAplicado = redondeado;
        OrigenMargen = Margen.OrigenMargen.Negociado;
        return true;
    }

    /// <summary>
    /// Versión nueva (ADR-018 §5, decisión del 2026-10-06): un margen ajustado por el comercial en la ruta equivalente de la versión
    /// anterior se copia (el envío lo conserva y solo refresca el sugerido).
    /// </summary>
    public void CopiarMargenNegociado(clsLineaSolicitud anterior)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        if (!anterior.TieneMargenNegociado)
        {
            return;
        }

        MargenPoliticaSugerido = anterior.MargenPoliticaSugerido;
        MargenSAPSugerido = anterior.MargenSAPSugerido;
        MargenAplicado = anterior.MargenAplicado;
        OrigenMargen = Margen.OrigenMargen.Negociado;
        MetodoCalculo = anterior.MetodoCalculo;
    }

    private void FijarSugeridos(decimal? margenPolitica, decimal? margenAplicado, OrigenMargen? origen)
    {
        MargenPoliticaSugerido = margenPolitica ?? (origen == Margen.OrigenMargen.PoliticaLinea ? margenAplicado : null);
        MargenSAPSugerido = origen == Margen.OrigenMargen.SapTm ? margenAplicado : null;
    }

    private static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static string? NombreMunicipio(string? nombre)
    {
        var limpio = Normalizar(nombre);
        return limpio is { Length: > LargoMaximoNombreMunicipio } ? limpio[..LargoMaximoNombreMunicipio] : limpio;
    }
}
