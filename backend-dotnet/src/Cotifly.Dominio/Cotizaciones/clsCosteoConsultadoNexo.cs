namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Foto del costeo de una solicitud en Nexo (ADR-015): el estado de la solicitud (<c>Pendiente</c>, <c>EnCosteo</c>,
/// <c>Entregada</c>, <c>Descartada</c>), los ids de sus líneas y la cotización de Nexo (nula si la solicitud está descartada).
/// </summary>
public sealed record clsCosteoConsultadoNexo(string EstadoSolicitud, IReadOnlyList<string> LineasSolicitud, clsCotizacionNexo? Cotizacion)
{
    public const string SolicitudEntregada = "Entregada";
    public const string SolicitudDescartada = "Descartada";

    /// <summary>
    /// Contenido de cada línea en Nexo (<c>lineas[]</c> de primer nivel de <c>SolicitudCosteoDto</c>: ruta, tipología, peso, vehículo y
    /// carrocería). Sirve para asociar las líneas de Cotifly con las de Nexo por contenido: Nexo no garantiza el orden de
    /// <c>solicitud.lineas[]</c> (ADR-017 §3). Vacío si Nexo no lo trae.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitudNexo> Detalle { get; init; } = [];

    /// <summary>
    /// ¿Los códigos de vehículo y carrocería del cubicador viajaron en las líneas (<c>Nexo:EnviarCodigosCubicador</c>)? Con
    /// <c>false</c>, el emparejador no compara el vehículo de los camiones del cubicador (su línea iba vacía). Por defecto <c>true</c>.
    /// </summary>
    public bool CodigosCubicadorEnviados { get; init; } = true;

    /// <summary>
    /// Los códigos de vehículo de Cotifly que viajaron traducidos por el diccionario de alias de Nexo (código de Cotifly →
    /// código enviado, <c>C2</c> → <c>2</c>), según lo registró el envío. El emparejamiento compara con el código enviado y la regla "la
    /// Mesa manda" no toma la traducción como un cambio (<see cref="clsEquivalenciasVehiculo"/>). Vacío si ninguno.
    /// </summary>
    public IReadOnlyDictionary<string, string> EquivalenciasVehiculo { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Una línea de la solicitud en Nexo (<c>clsLineaCosteoDto</c>): <paramref name="Ruta"/> es <c>"Origen → Destino"</c>.
/// <see cref="GrupoRutaId"/> es el id de la ruta de Cotifly que Nexo guardó y devuelve (<c>grupoRutaId</c>) y
/// <see cref="IdRecomendacionVehiculo"/> el camión del cubicador; <c>null</c> si la línea viajó sin ellos. No es el <c>rutaId</c> de
/// Nexo (su catálogo de rutas origen-destino), que Cotifly no usa.
/// </summary>
public sealed record clsLineaSolicitudNexo(string Id, string? Ruta, string? Tipologia, decimal? Peso, string? TipoVehiculo, string? Carroceria)
{
    /// <summary>Id de la ruta de Cotifly que Nexo guardó en la línea (<c>grupoRutaId</c>), o <c>null</c>.</summary>
    public string? GrupoRutaId { get; init; }

    /// <summary>Camión de la recomendación del cubicador que Nexo guardó en la línea (<c>idRecomendacionVehiculo</c>), o <c>null</c>.</summary>
    public string? IdRecomendacionVehiculo { get; init; }

    /// <summary>
    /// Posición de la línea en el arreglo del alta (<c>numeroLinea</c> del GET; desde 1), o <c>null</c> si Nexo no lo trae.
    /// Desempate fuerte entre camiones idénticos de un mismo <see cref="GrupoRutaId"/>.
    /// </summary>
    public int? NumeroLinea { get; init; }

    /// <summary>
    /// Estado de la línea en el costeo de Nexo (<c>estadoLinea</c>: <c>porCostear</c>, <c>autoResuelta</c>, <c>revisada</c> o
    /// <c>rechazada</c>), tal como lo manda Nexo, o <c>null</c> si Nexo no lo trae. No se usa para emparejar.
    /// </summary>
    public string? EstadoLinea { get; init; }

    /// <summary>
    /// Por qué la línea sigue en <c>porCostear</c> (<c>motivoPendiente</c>: consulta completa, línea sin vehículo, SICE-TAC
    /// que no es del mes actual...), o <c>null</c> si no está pendiente o Nexo no lo trae. Texto para el usuario. No se usa para emparejar.
    /// </summary>
    public string? MotivoPendiente { get; init; }

    /// <summary>
    /// Por qué la Mesa de Costeo rechazó la línea (<c>motivoRechazo</c>, hasta 500 caracteres), o <c>null</c> si
    /// la línea no está rechazada o Nexo no lo trae. Lo que decide el rechazo es <see cref="EstadoLinea"/> =
    /// <c>rechazada</c>; el motivo solo se muestra.
    /// </summary>
    public string? MotivoRechazo { get; init; }

    /// <summary>Lo que Nexo dice de la línea más allá de su contenido (estado, motivo pendiente y rechazo).</summary>
    public clsSituacionLineaNexo Situacion => clsSituacionLineaNexo.Desde(EstadoLinea, MotivoPendiente, MotivoRechazo);
}

/// <summary>
/// La situación de una línea de la solicitud en Nexo que Cotifly muestra por camión en <c>GET .../costeo</c>, sin guardarla en la
/// base: el estado de la línea en Nexo como código de Cotifly (<see cref="EstadoLinea"/>: <c>por_costear</c>, <c>auto_resuelta</c>,
/// <c>revisada</c>, <c>rechazada</c>; un valor nuevo de Nexo pasa con la misma conversión), el motivo por el que sigue pendiente
/// y el del rechazo (<see cref="MotivoRechazo"/>). <c>null</c> si Nexo no los manda. El rechazo, a diferencia del resto, SÍ se guarda:
/// el camión queda <see cref="Cotizaciones.EstadoCosteo.Rechazada"/> con su motivo (<see cref="clsVehiculoLineaSolicitud.MotivoRechazo"/>).
/// </summary>
public sealed record clsSituacionLineaNexo(string? EstadoLinea, string? MotivoPendiente)
{
    /// <summary>Código de Cotifly del estado de una línea rechazada por la Mesa de Costeo (<c>rechazada</c>).</summary>
    public const string EstadoRechazada = "rechazada";

    /// <summary>Largo máximo del motivo del rechazo (el de Nexo, <c>tbl_lineas_solicitud.MotivoRechazo nvarchar(500)</c>).</summary>
    public const int LargoMaximoMotivoRechazo = 500;

    /// <summary>
    /// El motivo del rechazo de la línea, o <c>null</c> si no está rechazada o Nexo no lo manda. Sin espacios
    /// sobrantes y recortado a <see cref="LargoMaximoMotivoRechazo"/>.
    /// </summary>
    public string? MotivoRechazo { get; init; }

    /// <summary>
    /// ¿La Mesa de Costeo rechazó la línea? (<see cref="EstadoLinea"/> = <c>rechazada</c>.) Sin el estado de Nexo nunca es un
    /// rechazo.
    /// </summary>
    public bool Rechazada => string.Equals(EstadoLinea, EstadoRechazada, StringComparison.Ordinal);

    /// <summary>Sin nada que mostrar (Nexo no mandó ni el estado ni los motivos).</summary>
    public bool Vacia => EstadoLinea is null && MotivoPendiente is null && MotivoRechazo is null;

    /// <summary>
    /// Id de la línea de Nexo de la que se leyó (<see cref="clsLineaSolicitudNexo.Id"/>), o <c>null</c> si no se sabe. La vista solo la
    /// muestra en el camión que sigue asociado a esa misma línea: tras una re-solicitud o un cambio de línea, la foto vieja no se muestra.
    /// </summary>
    public string? LineaNexoId { get; init; }

    /// <summary>Los estados conocidos de la línea en Nexo, por su forma compacta (solo letras y dígitos, en minúscula).</summary>
    private static readonly Dictionary<string, string> _estadosConocidos = new(StringComparer.Ordinal)
    {
        ["porcostear"] = "por_costear",
        ["autoresuelta"] = "auto_resuelta",
        ["revisada"] = "revisada",
        ["rechazada"] = EstadoRechazada,
    };

    /// <summary>
    /// La situación con los valores de Nexo: el estado convertido con <see cref="CodigoEstado"/> y el motivo sin espacios sobrantes (un
    /// texto vacío cuenta como <c>null</c>).
    /// </summary>
    public static clsSituacionLineaNexo Desde(string? estadoLineaNexo, string? motivoPendiente, string? motivoRechazo = null) =>
        new(CodigoEstado(estadoLineaNexo), string.IsNullOrWhiteSpace(motivoPendiente) ? null : motivoPendiente.Trim())
        {
            MotivoRechazo = NormalizarMotivoRechazo(motivoRechazo),
        };

    /// <summary>
    /// El motivo del rechazo sin espacios sobrantes (vacío = <c>null</c>) y, si pasa de
    /// <see cref="LargoMaximoMotivoRechazo"/> (Nexo ya lo limita), recortado con <c>…</c> para que quepa en <c>motivoRechazo</c> (script 10).
    /// </summary>
    public static string? NormalizarMotivoRechazo(string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo))
        {
            return null;
        }

        var limpio = motivo.Trim();
        return limpio.Length <= LargoMaximoMotivoRechazo ? limpio : limpio[..(LargoMaximoMotivoRechazo - 1)] + "…";
    }

    /// <summary>
    /// <c>estadoLinea</c> de Nexo como código de Cotifly en <c>snake_case</c>: <c>porCostear</c> o <c>PorCostear</c> →
    /// <c>por_costear</c>, <c>autoResuelta</c> → <c>auto_resuelta</c>, <c>revisada</c> → <c>revisada</c>, <c>rechazada</c> →
    /// <c>rechazada</c>. Vacío o <c>null</c> → <c>null</c>. Los cuatro estados conocidos se reconocen con cualquier mayúscula o
    /// separador (<c>PORCOSTEAR</c>, <c>por_costear</c>, <c>Por Costear</c>
    /// → <c>por_costear</c>); un valor nuevo pasa con la conversión de <c>camelCase</c> a <c>snake_case</c>.
    /// </summary>
    public static string? CodigoEstado(string? estadoLineaNexo)
    {
        if (string.IsNullOrWhiteSpace(estadoLineaNexo))
        {
            return null;
        }

        var texto = estadoLineaNexo.Trim();
        var compacto = string.Concat(texto.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (_estadosConocidos.TryGetValue(compacto, out var conocido))
        {
            return conocido;
        }

        var codigo = new System.Text.StringBuilder(texto.Length + 4);
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (c is ' ' or '-')
            {
                c = '_';
            }

            // Solo al pasar de minúscula o dígito a mayúscula: "EnRevision" → "en_revision", "ENREVISION" → "enrevision".
            if (char.IsUpper(c) && i > 0 && char.IsLetterOrDigit(texto[i - 1]) && !char.IsUpper(texto[i - 1]))
            {
                codigo.Append('_');
            }

            codigo.Append(char.ToLowerInvariant(c));
        }

        return codigo.ToString();
    }
}

/// <summary>
/// Un camión de Cotifly visto por el emparejador: la ruta (<c>"Origen → Destino"</c> con los nombres copiados), el id de la ruta
/// (<see cref="RutaId"/>: separa rutas iguales y es el <c>grupoRutaId</c> de Nexo), el camión del cubicador
/// (<see cref="IdRecomendacionVehiculo"/>) y lo que viajó a Nexo en su línea (tipología, vehículo, carrocería y peso).
/// </summary>
public sealed record clsCamionParaEmparejar(Guid Id, string Origen, string Destino, string? Tipologia, string? TipoVehiculo, string? Carroceria, decimal? PesoKg)
{
    /// <summary>Ruta de Cotifly del camión; <c>null</c> si quien arma el camión no la conoce (entonces la ruta es solo el texto).</summary>
    public Guid? RutaId { get; init; }

    /// <summary>Camión de la recomendación del cubicador; <c>null</c> sin recomendación.</summary>
    public Guid? IdRecomendacionVehiculo { get; init; }

    /// <summary>
    /// El camión <paramref name="camion"/> de <paramref name="ruta"/>, con el peso que REALMENTE viajó a Nexo en su línea
    /// (<see cref="clsLineaSolicitud.PesoLineaNexo"/>: con un solo camión, el de la ruta; con varios, el del camión). El
    /// emparejador compara con ese peso en una sola pasada; comparar primero con el del cubicador cruzaría costos entre rutas.
    /// </summary>
    /// <remarks>
    /// Con <paramref name="codigosCubicador"/> en <c>false</c> (<c>Nexo:EnviarCodigosCubicador</c> distinta de <c>siempre</c>) un camión del
    /// cubicador pudo viajar sin vehículo ni carrocería (o con ellos escritos como en Nexo): se empareja sin ellos (lo que la
    /// Mesa le ponga en Nexo no lo contradice). Con <paramref name="equivalencias"/> el camión se compara con el código
    /// que VIAJÓ (la traducción por alias, <see cref="clsEquivalenciasVehiculo.Enviado"/>), no con el que guarda Cotifly.
    /// </remarks>
    public static clsCamionParaEmparejar Desde(
        clsLineaSolicitud ruta, clsVehiculoLineaSolicitud camion, bool codigosCubicador = true, IReadOnlyDictionary<string, string>? equivalencias = null)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        ArgumentNullException.ThrowIfNull(camion);
        var sinCodigos = !codigosCubicador && camion.IdRecomendacionVehiculo is not null;
        return new clsCamionParaEmparejar(
            camion.Id, ruta.OrigenNombre ?? string.Empty, ruta.DestinoNombre ?? string.Empty, ruta.Tipologia,
            sinCodigos ? null : clsEquivalenciasVehiculo.Enviado(camion.TipoVehiculo, equivalencias), sinCodigos ? null : camion.Carroceria,
            ruta.PesoLineaNexo(camion))
        {
            RutaId = ruta.Id,
            IdRecomendacionVehiculo = camion.IdRecomendacionVehiculo,
        };
    }
}

/// <summary>
/// Asocia los camiones de Cotifly con las líneas de la solicitud de Nexo por CONTENIDO (ADR-017 §3, ADR-019), no por la
/// posición en la respuesta: Nexo carga las líneas sin <c>OrderBy</c> y sus ids son GUID aleatorios.
/// <list type="number">
/// <item><b>Camión del cubicador.</b> Si la línea de Nexo trae <c>grupoRutaId</c> e <c>idRecomendacionVehiculo</c>, se
/// asocia directo con el camión de esa ruta y esa recomendación: el par es único por camión, aun entre camiones idénticos, y no depende de
/// que el contenido cuadre (la Mesa pudo cambiar el vehículo). Dos líneas con el mismo par, o un par que no está en Cotifly, no se asocian
/// así: siguen por contenido.</item>
/// <item><b>Llave.</b> La ruta: el <c>grupoRutaId</c> de la ruta de Cotifly si Nexo lo devuelve o, si no, <c>"Origen → Destino"</c>
/// normalizado (mayúsculas, sin tildes ni espacios repetidos); y, cuando ambos lados lo traen, el peso, la tipología, el tipo de vehículo
/// y la carrocería. Un dato que solo trae un lado no contradice (la Mesa pudo ponerle vehículo a una línea que iba vacía).</item>
/// <item><b>Pares seguros.</b> Un camión y una línea que solo se pueden emparejar entre sí se asocian primero.</item>
/// <item><b>Empates.</b> Los camiones IDÉNTICOS de la misma ruta (misma llave) son intercambiables: valen lo mismo en Nexo. Si sus líneas
/// candidatas no las puede reclamar ningún otro camión, se asignan en orden estable: los camiones en el orden de envío (el de la lista
/// <c>camiones</c>) y las líneas por su id de Nexo (ordinal), así dos lecturas iguales dan la misma asignación. Sin el <c>grupoRutaId</c>, solo
/// si hay exactamente una línea candidata por camión: una de más puede ser la huérfana de otra ruta igual y cruzaría el costo. El riesgo que queda (sin
/// <c>grupoRutaId</c>) es que el ESTADO de dos camiones idénticos se cruce; el costo no, porque son iguales.</item>
/// <item><b>Sin asociar.</b> Camiones iguales de rutas DISTINTAS (que pueden costar distinto por datos que no viajan en la línea), datos
/// que se contradicen o un camión sin candidato quedan sin asociar: sin precio y con motivo.</item>
/// </list>
/// </summary>
public static class clsEmparejadorLineasNexo
{
    /// <summary>
    /// Ids de Nexo por id de camión de Cotifly, solo para los que se asocian (pares seguros y empates resueltos). <paramref name="camiones"/>
    /// van en el orden en que viajaron a Nexo (es el orden estable de los empates). <paramref name="yaAsignados"/> son los ids de Nexo que
    /// ya tienen dueño (no se reasignan).
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Emparejar(
        IReadOnlyList<clsCamionParaEmparejar> camiones, IReadOnlyList<clsLineaSolicitudNexo> nexo, IReadOnlySet<string>? yaAsignados = null) =>
        EmparejarConDetalle(camiones, nexo, yaAsignados).Pares;

    /// <summary>
    /// Como <see cref="Emparejar"/>, con lo que quedó pendiente para avisarlo: las líneas de Nexo sin dueño (huérfanas: de un camión
    /// descartado, sobrantes o de una ruta que no se pudo asociar) y los empates que no se resolvieron porque sus candidatas no cuadran.
    /// </summary>
    public static clsEmparejamiento EmparejarConDetalle(
        IReadOnlyList<clsCamionParaEmparejar> camiones, IReadOnlyList<clsLineaSolicitudNexo> nexo, IReadOnlySet<string>? yaAsignados = null)
    {
        ArgumentNullException.ThrowIfNull(camiones);
        ArgumentNullException.ThrowIfNull(nexo);
        var libres = nexo.Where(n => yaAsignados is null || !yaAsignados.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var pendientes = camiones.ToList();
        var resultado = new Dictionary<Guid, string>();
        var empates = new List<string>();
        bool avanzo;
        AsociarPorCamionCubicador(pendientes, libres, resultado);
        do
        {
            // Cada vuelta asocia al menos un par; un empate resuelto puede volver seguros otros pares.
            empates.Clear();
            avanzo = AsociarSeguros(pendientes, libres, resultado)
                     || AsociarRespaldoPorRutaYPeso(camiones, nexo, pendientes, libres, resultado)
                     || ResolverEmpate(pendientes, libres, resultado, empates);
        }
        while (avanzo && pendientes.Count > 0 && libres.Count > 0);


        return new clsEmparejamiento(resultado, [.. libres.Select(l => l.Id)], [.. pendientes.Select(c => c.Id)], empates);
    }

    /// <summary>
    /// Asocia las líneas de Nexo que traen <c>grupoRutaId</c> e <c>idRecomendacionVehiculo</c> con el único camión pendiente
    /// de esa ruta y esa recomendación. Un par repetido en Nexo o en Cotifly no se usa (lo resuelve el contenido).
    /// </summary>
    private static void AsociarPorCamionCubicador(List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado)
    {
        var conPar = libres
            .Select(n => (Linea: n, Par: ParCubicador(n)))
            .Where(x => x.Par is not null)
            .GroupBy(x => x.Par!.Value.Recomendacion)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .ToList();
        foreach (var (linea, par) in conPar)
        {
            // Una línea suelta (ruta de un solo camión) viaja con grupoRutaId = null; el camión del cubicador es único.
            var camiones = pendientes
                .Where(c => (par!.Value.Ruta is not { } ruta || c.RutaId == ruta) && c.IdRecomendacionVehiculo == par.Value.Recomendacion)
                .ToList();
            if (camiones.Count == 1)
            {
                Asociar(camiones[0], linea, pendientes, libres, resultado);
            }
        }
    }

    /// <summary>
    /// El par (<c>grupoRutaId</c>, <c>idRecomendacionVehiculo</c>) de la línea de Nexo, o <c>null</c> sin <c>idRecomendacionVehiculo</c>. Sin
    /// <c>grupoRutaId</c> (línea suelta) la ruta va en <c>null</c> y basta el camión del cubicador, que es único.
    /// </summary>
    private static (Guid? Ruta, Guid Recomendacion)? ParCubicador(clsLineaSolicitudNexo linea) =>
        Guid.TryParse(linea.IdRecomendacionVehiculo, out var recomendacion) && recomendacion != Guid.Empty
            ? (Guid.TryParse(linea.GrupoRutaId, out var ruta) ? ruta : null, recomendacion)
            : null;

    /// <summary>Asocia los pares únicos en ambos sentidos. Devuelve si asoció alguno.</summary>
    private static bool AsociarSeguros(List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado)
    {
        var avanzo = false;
        foreach (var camion in pendientes.ToList())
        {
            var candidatos = libres.Where(n => Coincide(camion, n)).ToList();
            if (candidatos.Count != 1 || pendientes.Count(c => Coincide(c, candidatos[0])) != 1)
            {
                continue;
            }

            Asociar(camion, candidatos[0], pendientes, libres, resultado);
            avanzo = true;
        }

        return avanzo;
    }

    /// <summary>
    /// Un camión de respaldo (sin <c>idRecomendacionVehiculo</c>: el del vehículo elegido o sin vehículo) se asocia por RUTA y
    /// PESO cuando esa combinación es única en la cotización (un solo camión pendiente y una sola línea libre la tienen), aunque el vehículo
    /// o la carrocería ya no cuadren: la Mesa de Costeo pudo cambiarlos en Nexo antes del primer polling ("la Mesa manda"). La línea no
    /// puede ser de un camión del cubicador (con <c>idRecomendacionVehiculo</c>) ni contradecir la tipología. Devuelve si asoció alguno.
    /// <para>La unicidad se mide sobre TODOS los camiones y TODAS las líneas de la entrada (<paramref name="todosCamiones"/>,
    /// <paramref name="todasLineas"/>), no sobre los que van quedando: si no, un par por contenido equivocado entre dos rutas con el mismo
    /// origen, destino y peso (sin <c>grupoRutaId</c>, la línea suelta) dejaría "única" la línea de la otra ruta y el costo se cruzaría en cascada.</para>
    /// </summary>
    private static bool AsociarRespaldoPorRutaYPeso(
        IReadOnlyList<clsCamionParaEmparejar> todosCamiones, IReadOnlyList<clsLineaSolicitudNexo> todasLineas,
        List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado)
    {
        var avanzo = false;
        foreach (var camion in pendientes.Where(c => c.IdRecomendacionVehiculo is null && c.PesoKg is not null).ToList())
        {
            var candidatas = libres.Where(n => Recomendacion(n) is null && PuedeSerDe(camion, n)).ToList();
            if (candidatas.Count != 1 || pendientes.Count(c => PuedeSerDe(c, candidatas[0])) != 1
                || todasLineas.Count(n => Recomendacion(n) is null && PuedeSerDe(camion, n)) != 1
                || todosCamiones.Count(c => PuedeSerDe(c, candidatas[0])) != 1)
            {
                continue;
            }

            Asociar(camion, candidatas[0], pendientes, libres, resultado);
            avanzo = true;
        }

        return avanzo;
    }

    /// <summary>
    /// ¿La línea de Nexo puede ser de este camión sin mirar el vehículo? La misma ruta, el mismo peso, sin contradecir la
    /// tipología y, si la línea trae <c>idRecomendacionVehiculo</c>, el del camión. Un camión del cubicador cuenta como posible dueño de una
    /// línea sin recomendación (Nexo puede no devolverla): así la unicidad nunca le quita su línea.
    /// </summary>
    private static bool PuedeSerDe(clsCamionParaEmparejar camion, clsLineaSolicitudNexo nexo) =>
        camion.PesoKg is { } peso && nexo.Peso is { } pesoNexo && Math.Round(peso, 2) == Math.Round(pesoNexo, 2)
        && (Recomendacion(nexo) is not { } recomendacion || camion.IdRecomendacionVehiculo == recomendacion)
        && MismaRuta(camion, nexo)
        && Compatible(camion.Tipologia, nexo.Tipologia);

    private static Guid? Recomendacion(clsLineaSolicitudNexo nexo) =>
        Guid.TryParse(nexo.IdRecomendacionVehiculo, out var recomendacion) && recomendacion != Guid.Empty ? recomendacion : null;

    /// <summary>
    /// Resuelve el primer grupo de camiones idénticos de una misma ruta (en orden de envío) cuyas líneas candidatas no puede reclamar
    /// ningún otro camión: las asigna en orden estable. Sin el <c>grupoRutaId</c> de Nexo en todas las candidatas, solo si hay
    /// EXACTAMENTE una candidata por camión: una candidata de más puede ser la línea huérfana de otra ruta con el mismo origen y destino (un
    /// camión descartado o sobrante) y tomarla cruzaría el costo entre rutas; ese empate queda sin resolver y se avisa en
    /// <paramref name="empates"/>. Devuelve si asoció alguno.
    /// </summary>
    private static bool ResolverEmpate(
        List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado, List<string> empates)
    {
        foreach (var grupo in pendientes.GroupBy(Firma).Select(g => g.ToList()))
        {
            var candidatos = libres.Where(n => Coincide(grupo[0], n)).ToList();
            if (candidatos.Count == 0 || pendientes.Except(grupo).Any(otro => candidatos.Any(n => Coincide(otro, n))))
            {
                continue;
            }

            var conReferencia = grupo[0].RutaId is { } rutaId
                                && candidatos.All(n => Guid.TryParse(n.GrupoRutaId, out var referencia) && referencia == rutaId);
            if (!conReferencia && candidatos.Count != grupo.Count)
            {
                empates.Add($"{grupo.Count} camiones idénticos de la ruta {grupo[0].Origen} → {grupo[0].Destino} con {candidatos.Count} líneas candidatas en Nexo");
                continue;
            }

            // Con el grupo y numeroLinea en todas las candidatas, el orden del alta manda (desempate fuerte); si no, el id.
            if (conReferencia && candidatos.All(n => n.NumeroLinea is not null))
            {
                candidatos = [.. candidatos.OrderBy(n => n.NumeroLinea)];
            }

            foreach (var (camion, linea) in grupo.Zip(candidatos))
            {
                Asociar(camion, linea, pendientes, libres, resultado);
            }

            return true;
        }

        return false;
    }

    private static void Asociar(
        clsCamionParaEmparejar camion, clsLineaSolicitudNexo linea, List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres,
        Dictionary<Guid, string> resultado)
    {
        resultado[camion.Id] = linea.Id;
        pendientes.Remove(camion);
        libres.Remove(linea);
    }

    /// <summary>Lo que hace idénticos a dos camiones: la misma ruta de Cotifly y los mismos datos de su línea en Nexo.</summary>
    private static (string Ruta, string Tipologia, string Vehiculo, string Carroceria, decimal? Peso) Firma(clsCamionParaEmparejar camion) =>
        (camion.RutaId?.ToString("D") ?? Normalizar($"{camion.Origen} → {camion.Destino}"),
         NormalizarOVacio(camion.Tipologia), NormalizarOVacio(camion.TipoVehiculo), NormalizarOVacio(camion.Carroceria),
         camion.PesoKg is { } peso ? Math.Round(peso, 2) : null);

    /// <summary>
    /// ¿La línea de Nexo puede ser este camión de Cotifly? Misma ruta (por el <c>grupoRutaId</c> si ambos lados lo traen, si no por el texto
    /// <c>"Origen → Destino"</c>), el mismo camión del cubicador si ambos lo traen y sin contradicción en los datos que traen ambos.
    /// </summary>
    public static bool Coincide(clsCamionParaEmparejar camion, clsLineaSolicitudNexo nexo)
    {
        ArgumentNullException.ThrowIfNull(camion);
        ArgumentNullException.ThrowIfNull(nexo);
        if (camion.IdRecomendacionVehiculo is { } recomendacion && Guid.TryParse(nexo.IdRecomendacionVehiculo, out var deNexo) && deNexo != recomendacion)
        {
            return false; // La línea es de otro camión del cubicador.
        }

        if (!MismaRuta(camion, nexo))
        {
            return false;
        }

        return Compatible(camion.Tipologia, nexo.Tipologia)
               && Compatible(camion.TipoVehiculo, nexo.TipoVehiculo)
               && Compatible(camion.Carroceria, nexo.Carroceria)
               && (camion.PesoKg is not { } peso || nexo.Peso is not { } pesoNexo || Math.Round(peso, 2) == Math.Round(pesoNexo, 2));
    }

    /// <summary>La misma ruta: por el <c>grupoRutaId</c> si ambos lados lo traen; si no, por el texto <c>"Origen → Destino"</c> normalizado.</summary>
    private static bool MismaRuta(clsCamionParaEmparejar camion, clsLineaSolicitudNexo nexo) =>
        camion.RutaId is { } rutaId && Guid.TryParse(nexo.GrupoRutaId, out var referencia)
            ? referencia == rutaId
            : nexo.Ruta is not null && Normalizar(nexo.Ruta) == Normalizar($"{camion.Origen} → {camion.Destino}");

    /// <summary>
    /// Un dato que solo trae un lado no contradice; si los dos lo traen, se comparan sin tildes, mayúsculas ni separadores (Nexo
    /// devuelve <c>Furgón</c> o <c>FURGON REFRIGERADO</c> por los códigos <c>FURGON</c> y <c>FURGON_REFRIGERADO</c>).
    /// </summary>
    private static bool Compatible(string? cotifly, string? nexo) =>
        string.IsNullOrWhiteSpace(cotifly) || string.IsNullOrWhiteSpace(nexo) || clsVehiculoLineaSolicitud.MismoCodigo(cotifly, nexo);

    private static string NormalizarOVacio(string? texto) => string.IsNullOrWhiteSpace(texto) ? string.Empty : Normalizar(texto);

    /// <summary>Mayúsculas, sin tildes, la flecha y los espacios normalizados.</summary>
    public static string Normalizar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var sinTildes = new string(texto.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        return string.Join(' ', sinTildes.Replace("->", "→", StringComparison.Ordinal).ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Resultado del emparejamiento: los pares camión → línea de Nexo, las líneas de Nexo que quedaron sin dueño, los camiones sin
/// asociar y los empates que no se resolvieron (texto para el aviso).
/// </summary>
public sealed record clsEmparejamiento(
    IReadOnlyDictionary<Guid, string> Pares, IReadOnlyList<string> LineasSinDueno, IReadOnlyList<Guid> CamionesSinAsociar,
    IReadOnlyList<string> EmpatesSinResolver);

/// <summary><c>CotizacionDto</c> de Nexo reducido a lo que usa Cotifly.</summary>
/// <param name="Estado"><c>Borrador</c> | <c>Entregada</c>.</param>
/// <param name="Resultado"><c>SinDefinir</c> | <c>Ganada</c> | <c>Perdida</c>.</param>
public sealed record clsCotizacionNexo(
    string Id, string Estado, string Resultado, IReadOnlyList<clsLineaCosteoNexo> Lineas, IReadOnlyList<clsConexoNexo> Conexos)
{
    public const string EstadoEntregada = "Entregada";
    public const string EstadoBorrador = "Borrador";
}

/// <summary>
/// Línea con flete de la cotización de Nexo (<c>CotizacionLineaDto</c>). <paramref name="Id"/> es el id de la línea de la COTIZACIÓN de
/// Nexo (<c>clsCotizacionLineaDto.id</c>), distinto de <paramref name="LineaSolicitudId"/> (la línea de la solicitud): es el
/// <c>lineaId</c> que pide <c>POST /api/cotizaciones/{id}/conexos</c> (ADR-018 §4). Se lee cada vez que se lee la
/// cotización de Nexo; <c>null</c> si Nexo no lo trae.
/// </summary>
public sealed record clsLineaCosteoNexo(
    string? LineaSolicitudId, decimal ValorFlete, decimal? ValorAprovisionamiento, decimal? ValorRetornoContenedor, string? Id = null)
{
    /// <summary>Costo de la línea: flete + aprovisionamiento + retorno del contenedor (lo que suma Nexo en su total).</summary>
    public decimal Costo => ValorFlete + (ValorAprovisionamiento ?? 0m) + (ValorRetornoContenedor ?? 0m);
}

/// <summary>
/// Servicio conexo de la cotización de Nexo (<c>CotizacionConexoDto</c>): <paramref name="ValorAplicado"/> es un costo (costo unitario ×
/// cantidad, interno). La venta (contrato de Nexo §7.1) viaja aparte en <see cref="PrecioVentaUnitario"/> y
/// <see cref="ValorVenta"/>.
/// </summary>
public sealed record clsConexoNexo(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal ValorAplicado)
{
    /// <summary>
    /// <c>precioVentaUnitario</c>, el precio de venta de UNA unidad que Nexo fija al agregar el conexo (el de su biblioteca
    /// o, si no tenía, el costo). <c>null</c> si Nexo no lo trae.
    /// </summary>
    public decimal? PrecioVentaUnitario { get; init; }

    /// <summary>
    /// <c>valorVenta</c> = <c>precioVentaUnitario × cantidad</c>: lo que se cotiza al cliente, sin margen. Nexo lo manda
    /// siempre; <c>null</c> solo si Nexo no lo trae: el conexo se cotiza entonces al costo, no desaparece.
    /// </summary>
    public decimal? ValorVenta { get; init; }

    /// <summary>
    /// La venta unitaria del conexo (contrato de Nexo §7.1): <see cref="PrecioVentaUnitario"/> (la que Nexo fijó al
    /// agregarlo) o, si no viene, <c>valorVenta ÷ cantidad</c>; a 2 decimales half-to-even, como el costo. Sin ninguno de los dos
    /// o con un valor negativo, <c>null</c>: no se conoce (el conexo nuevo se cotiza al costo y uno guardado
    /// conserva su venta). Una venta 0 es un conexo sin cobro. Nunca hace desaparecer el conexo.
    /// </summary>
    public decimal? VentaUnitaria
    {
        get
        {
            if (PrecioVentaUnitario is >= 0m and var unitaria)
            {
                return decimal.Round(unitaria, 2, MidpointRounding.ToEven);
            }

            var cantidad = Cantidad > 0m ? decimal.Round(Cantidad, 2) : 1m;
            return ValorVenta is >= 0m and var venta ? decimal.Round(venta / cantidad, 2, MidpointRounding.ToEven) : null;
        }
    }

    /// <summary>
    /// Línea de la COTIZACIÓN de Nexo a la que está pegado (<c>cotizacionLineaId</c>): Cotifly lo pega a la línea del primer camión de su
    /// ruta, así que esta línea dice de qué ruta es. <c>null</c> si Nexo no lo trae.
    /// </summary>
    public string? CotizacionLineaId { get; init; }

    /// <summary>
    /// Servicio del catálogo de Nexo (<c>servicioConexoId</c>): el mismo id que guarda Cotifly en
    /// <see cref="clsServicioConexoRuta.IdServicioConexo"/> para los conexos capturados con la ruta. Con él se sabe si un conexo de Cotifly
    /// ya está en la cotización de Nexo. <c>null</c> si Nexo no lo trae.
    /// </summary>
    public string? ServicioConexoId { get; init; }

    /// <summary>
    /// La <c>categoria</c> tal como la mandó Nexo, o <c>null</c> si no la trae (<see cref="Categoria"/> usa entonces
    /// <c>logistico</c>). Es la que se guarda en <c>categoriaServicioConexo</c> (script 05): un valor por defecto no se guarda como dato.
    /// </summary>
    public string? CategoriaInformada { get; init; }
}

/// <summary>
/// Derivación de <c>estado_costeo</c> (ADR-015 §4), la misma regla de la bandeja de Nexo
/// (<c>EfSolicitudesCatalogoRepository</c>): cruzar las líneas de la solicitud contra las líneas de la cotización por
/// <c>LineaSolicitudId</c>.
/// </summary>
public static class clsDerivacionEstadoCosteo
{
    /// <summary>
    /// <c>Descartada</c> → <see cref="EstadoCosteo.Descartada"/>; cotización (o solicitud) <c>Entregada</c> →
    /// <see cref="EstadoCosteo.Entregada"/>; toda línea de la solicitud con flete → <see cref="EstadoCosteo.Autocosteo"/>; si no,
    /// <see cref="EstadoCosteo.PendienteCosteo"/>. Una solicitud sin líneas nunca es autocosteo. Las líneas que la Mesa
    /// rechazó (<see cref="LineaRechazada"/>) no se esperan: <c>autocosteo</c> cuando todas las NO rechazadas tienen flete. Si todas están
    /// rechazadas y Nexo aún no entrega, <c>pendiente_costeo</c> (no hay nada que cotizar todavía: la Mesa puede deshacer un rechazo o
    /// entregar la cotización en $0, y el polling sigue hasta verlo).
    /// </summary>
    public static EstadoCosteo Derivar(clsCosteoConsultadoNexo foto)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.Equals(foto.EstadoSolicitud, clsCosteoConsultadoNexo.SolicitudDescartada, StringComparison.OrdinalIgnoreCase))
        {
            return EstadoCosteo.Descartada;
        }

        if (string.Equals(foto.Cotizacion?.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase)
            || string.Equals(foto.EstadoSolicitud, clsCosteoConsultadoNexo.SolicitudEntregada, StringComparison.OrdinalIgnoreCase))
        {
            return EstadoCosteo.Entregada;
        }

        var costeadas = (foto.Cotizacion?.Lineas ?? []).Select(l => l.LineaSolicitudId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var porCostear = foto.LineasSolicitud.Where(id => !LineaRechazada(foto, id)).ToList();
        return porCostear.Count > 0 && porCostear.All(costeadas.Contains)
            ? EstadoCosteo.Autocosteo
            : EstadoCosteo.PendienteCosteo;
    }

    /// <summary>
    /// ¿La Mesa de Costeo rechazó la línea <paramref name="lineaSolicitudNexoId"/>? Lo dice el
    /// <c>estadoLinea</c> = <c>rechazada</c> del detalle de la foto (<see cref="clsCosteoConsultadoNexo.Detalle"/>). Sin detalle o sin el
    /// estado (por ejemplo, una foto armada con la respuesta del resultado) no hay rechazo.
    /// </summary>
    public static bool LineaRechazada(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId) =>
        SituacionLinea(foto, lineaSolicitudNexoId)?.Rechazada == true;

    /// <summary>Lo que Nexo dice de la línea <paramref name="lineaSolicitudNexoId"/> en el detalle de la foto, o <c>null</c> si no está.</summary>
    public static clsSituacionLineaNexo? SituacionLinea(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        return string.IsNullOrWhiteSpace(lineaSolicitudNexoId)
            ? null
            : foto.Detalle.FirstOrDefault(d => string.Equals(d.Id, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase))?.Situacion;
    }

    /// <summary>
    /// Estado de UNA línea (ADR-017), con la misma regla: <c>Descartada</c> → <see cref="EstadoCosteo.Descartada"/>;
    /// cotización (o solicitud) <c>Entregada</c> → <see cref="EstadoCosteo.Entregada"/>; la línea con flete →
    /// <see cref="EstadoCosteo.Autocosteo"/>; si no, <see cref="EstadoCosteo.PendienteCosteo"/>. Una entregada sin flete para la
    /// línea queda <c>entregada</c> sin costo (Nexo la entregó sin esa línea): <see cref="CostoLinea"/> lo distingue. Una
    /// línea que la Mesa rechazó es <see cref="EstadoCosteo.Rechazada"/> (también con la cotización entregada; con la solicitud descartada
    /// manda <c>descartada</c>).
    /// </summary>
    public static EstadoCosteo DerivarLinea(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        var cotizacion = Derivar(foto);
        if (cotizacion is EstadoCosteo.Descartada)
        {
            return cotizacion;
        }

        if (LineaRechazada(foto, lineaSolicitudNexoId))
        {
            return EstadoCosteo.Rechazada;
        }

        if (cotizacion is EstadoCosteo.Entregada)
        {
            return cotizacion;
        }

        return CostoLinea(foto, lineaSolicitudNexoId) is null ? EstadoCosteo.PendienteCosteo : EstadoCosteo.Autocosteo;
    }

    /// <summary>
    /// Costo de Nexo de una línea (suma de las líneas de la cotización de Nexo con ese <c>lineaSolicitudId</c>: flete +
    /// aprovisionamiento + retorno), o <c>null</c> si la línea no tiene flete.
    /// </summary>
    public static decimal? CostoLinea(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.IsNullOrWhiteSpace(lineaSolicitudNexoId) || foto.Cotizacion is not { } cotizacion)
        {
            return null;
        }

        var lineas = cotizacion.Lineas.Where(l => string.Equals(l.LineaSolicitudId, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase)).ToList();
        return lineas.Count == 0 ? null : lineas.Sum(l => l.Costo);
    }

    /// <summary>
    /// Flete y retorno de Nexo de un camión (suma de las líneas de la cotización de Nexo con ese <c>lineaSolicitudId</c>; el retorno 0 si
    /// Nexo no lo trae), o <c>null</c> si no tiene flete. El levante NO sale de Nexo: es la copia de Cotifly.
    /// </summary>
    public static (decimal Flete, decimal Retorno)? CostosCamion(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.IsNullOrWhiteSpace(lineaSolicitudNexoId) || foto.Cotizacion is not { } cotizacion)
        {
            return null;
        }

        var lineas = cotizacion.Lineas.Where(l => string.Equals(l.LineaSolicitudId, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase)).ToList();
        return lineas.Count == 0 ? null : (lineas.Sum(l => l.ValorFlete), lineas.Sum(l => l.ValorRetornoContenedor ?? 0m));
    }

    /// <summary>
    /// ¿El polling deja de consultar en este estado? (<c>entregada</c> y <c>descartada</c> son terminales.) <c>rechazada</c>
    /// NO es terminal (la Mesa puede deshacer el rechazo antes de entregar); además la cabecera nunca queda en ese estado.
    /// </summary>
    public static bool EsTerminal(EstadoCosteo estado) => estado is EstadoCosteo.Entregada or EstadoCosteo.Descartada or EstadoCosteo.CosteoVencido;

    /// <summary>¿El costeo está completo y se puede poner precio? (<c>autocosteo</c> o <c>entregada</c>.)</summary>
    public static bool TieneCosteo(EstadoCosteo estado) => estado is EstadoCosteo.Autocosteo or EstadoCosteo.Entregada;
}
