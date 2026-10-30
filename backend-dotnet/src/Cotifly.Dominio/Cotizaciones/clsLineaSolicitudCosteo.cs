using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Códigos por campo del reporte de validación de una licitación (HU-011.2, <c>docs/conexion_nexo/HU-011-formato-excel-licitacion.md</c> §5).
/// Espejo de <c>CapturarSolicitudValidador</c> y de los atributos de <c>CapturaLineaRequest</c> de Nexo; viajan en
/// <c>errores[].codigo</c>, no como <c>ErrorNegocio</c>.
/// </summary>
public static class clsCodigosLineaSolicitud
{
    public const string LineasRequeridas = "LINEAS_REQUERIDAS";
    public const string DemasiadasLineas = "DEMASIADAS_LINEAS";
    public const string LineaIncompleta = "LINEA_INCOMPLETA";
    public const string CampoDemasiadoLargo = "CAMPO_DEMASIADO_LARGO";
    public const string ValorFueraDeRango = "VALOR_FUERA_DE_RANGO";
    public const string DemasiadasPosiciones = "DEMASIADAS_POSICIONES";
    public const string PosicionInvalida = "POSICION_INVALIDA";
    public const string PesoRequerido = clsSolicitudCosteo.PesoRequerido;
    public const string PesoInconsistente = clsSolicitudCosteo.PesoInconsistente;
    public const string FilaInvalida = "FILA_INVALIDA";
    public const string CampoRequerido = "CAMPO_REQUERIDO";

    /// <summary>G3 (HU-013.11): el campo no aplica con los demás valores de la línea (Nexo lo descartaría).</summary>
    public const string CampoNoAplica = "CAMPO_NO_APLICA";

    /// <summary>G2 (HU-013.11): el valor no es un código de catálogo (texto libre, o mayúsculas distintas del código).</summary>
    public const string CodigoNoCatalogo = "CODIGO_NO_CATALOGO";

    /// <summary>G6 (HU-013.11): el texto del origen o destino coincide con varios municipios del catálogo de Nexo.</summary>
    public const string MunicipioAmbiguo = "MUNICIPIO_AMBIGUO";

    /// <summary>G6 (HU-013.11): el texto del origen o destino no coincide con ningún municipio del catálogo de Nexo.</summary>
    public const string MunicipioNoEncontrado = "MUNICIPIO_NO_ENCONTRADO";

    /// <summary>HU-021.6 (decisión 2): el id de municipio que llegó no existe en el catálogo de municipios de Nexo.</summary>
    public const string MunicipioInexistente = "MUNICIPIO_INEXISTENTE";

    /// <summary>HU-021.6: la ruta pide dos veces el mismo servicio conexo (la cantidad ya dice cuántos son).</summary>
    public const string ConexoDuplicado = "CONEXO_DUPLICADO";

    /// <summary>HU-021.6: el servicio conexo no está (activo) en el catálogo de Nexo.</summary>
    public const string ConexoNoEncontrado = "CONEXO_NO_ENCONTRADO";

    /// <summary>HU-021.6: la ruta pide más servicios conexos de los admitidos.</summary>
    public const string DemasiadosConexos = "DEMASIADOS_CONEXOS";

    /// <summary>HU-021.5 (#133): el <c>id_tipo_mercancia</c> (obsoleto) no está en el catálogo del cubicador (<c>GET /web/tipos-mercancia</c>).</summary>
    public const string TipoMercanciaInexistente = "TIPO_MERCANCIA_INEXISTENTE";

    /// <summary>HU-023.3 (#158): el <c>codigo_mercancia</c> no está (activo) en el catálogo del cubicador.</summary>
    public const string MercanciaDesconocida = "MERCANCIA_DESCONOCIDA";

    /// <summary>HU-023.3 (#158): el catálogo del cubicador marca la mercancía como no cubicable.</summary>
    public const string MercanciaNoCubicable = "MERCANCIA_NO_CUBICABLE";

    /// <summary>
    /// HU-030.1 (#184): el <c>peso_con_tara_kg</c> de la ruta es menor que su peso de carga (<c>peso_kg</c> o, sin él, la suma de las
    /// mercancías): el peso con tara es la carga MÁS la tara de los contenedores. Con otra clase de viaje el campo es
    /// <see cref="CampoNoAplica"/>.
    /// </summary>
    public const string PesoConTaraMenorQueCarga = "PESO_CON_TARA_MENOR_QUE_CARGA";

    /// <summary>
    /// HU-032 (#197): la ruta no se puede cubicar (no todas sus mercancías son cubicables, o solo trae el peso) y no trae
    /// <c>tipo_vehiculo</c>. Salvo en Contenedor (Nexo deduce el vehículo), el vehículo elegido es obligatorio.
    /// </summary>
    public const string VehiculoRequerido = "VEHICULO_REQUERIDO";
}

/// <summary>Un error de un campo de la licitación: el campo (snake_case del contrato), su código y un mensaje legible.</summary>
public sealed record clsErrorCampo(string Campo, string Codigo, string Mensaje);

/// <summary>
/// Encabezado de una solicitud de Nexo (<c>CapturaSolicitudRequest</c> sin las líneas): uno por cotización, con los límites de
/// Nexo. Lo comparten el formulario de una línea (<see cref="clsSolicitudCosteo"/>) y las licitaciones (HU-011, ADR-017).
/// </summary>
public sealed record clsEncabezadoSolicitudCosteo
{
    public required TipoSolicitud Tipo { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary><c>notas</c> de Nexo (máx. 2000): nota para la Mesa, distinta de la personalización del PDF (G7, HU-013.10).</summary>
    public string? Notas { get; init; }

    /// <summary>Todos los errores del encabezado (vacía si Nexo lo aceptaría).</summary>
    public IReadOnlyList<clsErrorCampo> Errores()
    {
        var errores = new List<clsErrorCampo>();
        clsLineaSolicitudCosteo.Largo(errores, "notas", Notas, clsSolicitudCosteo.LargoNotas);
        clsLineaSolicitudCosteo.Rango(errores, "horas_cargue", HorasCargue, 0m, clsSolicitudCosteo.MaximoHoras);
        clsLineaSolicitudCosteo.Rango(errores, "horas_descargue", HorasDescargue, 0m, clsSolicitudCosteo.MaximoHoras);
        return errores;
    }
}

/// <summary>
/// Una línea de la solicitud de Nexo (<c>CapturaLineaRequest</c>): origen, destino, catálogos con el código de Nexo tal cual,
/// pesos, valores y posiciones de cubicaje. Una licitación lleva N (HU-011.2); el formulario de HU-005.5, una.
/// </summary>
public sealed record clsLineaSolicitudCosteo
{
    /// <summary>
    /// DEF-1027B-3 (#228): nombre del contrato del valor de un levante (<c>rutas[]</c> de la captura y filas de la licitación); los errores
    /// de <see cref="ValorAprovisionamiento"/> lo nombran así.
    /// </summary>
    public const string CampoValorLevante = "valor_levante_contenedor";

    /// <summary>Nombre anterior de <see cref="CampoValorLevante"/> (formato plano y <c>datos_carga</c>); solo se menciona en el mensaje.</summary>
    public const string CampoValorLevanteAnterior = "valor_aprovisionamiento";

    public required string Origen { get; init; }
    public required string Destino { get; init; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo del origen (<c>origenMunicipioId</c>, G6); <c>null</c> si vino como texto.</summary>
    public int? OrigenMunicipioId { get; init; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo del destino (<c>destinoMunicipioId</c>, G6).</summary>
    public int? DestinoMunicipioId { get; init; }

    public string? TipoVehiculo { get; init; }
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }

    /// <summary>Nombre del municipio donde se devuelve el contenedor (copia, decisión 2 de HU-021).</summary>
    public string? CiudadDevolucionContenedor { get; init; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo de la ciudad de devolución del contenedor (HU-021.6, decisión 2).</summary>
    public int? CiudadDevolucionMunicipioId { get; init; }

    public bool? LevanteContenedorDiferente { get; init; }

    /// <summary>Nombre del municipio donde se levanta el contenedor (copia, decisión 2 de HU-021).</summary>
    public string? CiudadLevanteContenedor { get; init; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo de la ciudad de levante del contenedor (HU-021.6, decisión 2).</summary>
    public int? CiudadLevanteMunicipioId { get; init; }

    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>
    /// HU-030.1 (#184): peso total CON la tara de los contenedores que escribe el comercial para la ruta (<c>peso_con_tara_kg</c>, kg);
    /// <c>null</c> = sin tara (a Nexo va el peso de carga, como antes). Solo con <c>clase_viaje = Contenedor</c> y nunca menor que el peso
    /// de carga (<see cref="PesoCarga"/>). No va al cubicador (que recibe el peso por mercancía, sin tara): solo cambia el <c>peso</c> de
    /// las líneas de Nexo de la ruta, que entonces viajan sin posiciones (<see cref="clsLineaSolicitud.PesoLineaNexo"/>).
    /// </summary>
    public decimal? PesoConTaraKg { get; init; }

    /// <summary>¿La ruta es de contenedor (<c>clase_viaje = Contenedor</c>, recortado, como se guarda)?</summary>
    public bool EsContenedor => string.Equals(ClaseViaje?.Trim(), clsCatalogosCaptura.ClaseViajeContenedor, StringComparison.Ordinal);

    /// <summary>Peso de la carga (sin tara): <see cref="PesoKg"/> o, sin él, la suma de las mercancías con peso; <c>null</c> si no hay.</summary>
    public decimal? PesoCarga => PesoKg ?? (SumaPesoPosiciones is var suma && suma > 0 ? suma : null);

    /// <summary>
    /// Horas de cargue propias de la línea (<c>horasCargue</c> de <c>CapturaLineaRequest</c>, G5 de HU-013.11); <c>null</c> = las del
    /// encabezado. Solo las licitaciones las traen por fila; el formulario de una línea usa las del encabezado.
    /// </summary>
    public decimal? HorasCargue { get; init; }

    /// <summary>Horas de descargue propias de la línea (<c>horasDescargue</c>); <c>null</c> = las del encabezado.</summary>
    public decimal? HorasDescargue { get; init; }

    /// <summary>Nombre del arreglo de mercancías en el contrato web (<c>mercancias[]</c>): los errores por mercancía lo usan con índice base 0.</summary>
    public const string CampoMercancias = "mercancias";

    /// <summary>Posiciones de cubicaje (bultos), en el orden del formulario o del pliego.</summary>
    public IReadOnlyList<clsPosicionCarga> Posiciones { get; init; } = [];

    /// <summary>
    /// Ruta de Cotifly de la que sale la línea (HU-021.7, #134; petición N1 a Nexo): solo la llena <see cref="clsLineaSolicitud.ALineaNexo"/>
    /// al armar una línea por camión. No la captura el comercial ni se valida. Viaja a Nexo como <c>grupoRutaId</c> (HU-023.4, #159) solo
    /// con <c>Nexo:EnviarGrupoRutaId</c>; mientras tanto, el emparejamiento usa el contenido de la línea y el orden de envío.
    /// </summary>
    public Guid? RutaId { get; init; }

    /// <summary>
    /// Camión de la recomendación del cubicador del que sale la línea (HU-023.4, #159): solo la llena
    /// <see cref="clsLineaSolicitud.ALineaNexo"/>. Viaja a Nexo como <c>idRecomendacionVehiculo</c> (texto, hasta 64) solo con
    /// <c>Nexo:EnviarDatosCubicador</c>. <c>null</c> en un camión sin recomendación (respaldo o "sin selección automática").
    /// </summary>
    public Guid? IdRecomendacionVehiculo { get; init; }

    /// <summary>
    /// Línea de negocio de Nexo de la ruta (<c>lineaNegocio</c> de <c>CapturaLineaRequest</c>, #134): la llena el envío con el
    /// <c>mapeoLineasNexo</c> de la línea de negocio de SU ruta. <c>null</c> = la de la solicitud. No la captura el comercial.
    /// </summary>
    public string? LineaNegocioNexo { get; init; }

    /// <summary>Suma <c>cantidad × pesoKg</c> de las posiciones con peso (la misma de Nexo).</summary>
    public decimal SumaPesoPosiciones => Posiciones.Where(p => p?.PesoKg is > 0).Sum(p => p.Cantidad * p.PesoKg!.Value);

    /// <summary>
    /// HU-032 (#197): cómo se cubica la ruta según sus mercancías (<see cref="clsPosicionCarga.EsCubicable"/>). Solo
    /// <see cref="CubicabilidadRuta.Cubicable"/> llama al cubicador; en los demás casos la ruta usa el vehículo elegido.
    /// </summary>
    public CubicabilidadRuta Cubicabilidad
    {
        get
        {
            var mercancias = Posiciones.Where(p => p is not null).ToList();
            if (mercancias.Count == 0)
            {
                return CubicabilidadRuta.SinMercancias;
            }

            var cubicables = mercancias.Count(p => p.EsCubicable);
            return cubicables == mercancias.Count ? CubicabilidadRuta.Cubicable
                : cubicables == 0 ? CubicabilidadRuta.NoCubicable
                : CubicabilidadRuta.Mixta;
        }
    }

    /// <summary>
    /// HU-032 (#197): ¿la ruta exige el vehículo elegido? Sí cuando no se puede cubicar (no todas sus mercancías son cubicables) y su clase
    /// de viaje no es una de <paramref name="clasesSinVehiculo"/> (<c>Captura:ClasesSinVehiculoObligatorio</c>; <c>null</c> = solo
    /// <see cref="clsCatalogosCaptura.ClaseViajeContenedor"/>, el valor por defecto): en Cupo, Nexo no autocostea una línea sin vehículo
    /// (COTIFLY-CONTRATO §4); en Contenedor lo deduce por peso y clase. La clase se compara recortada y exacta, como se guarda y como la
    /// valida el catálogo de Nexo.
    /// </summary>
    public bool ExigeVehiculoElegido(IReadOnlyCollection<string>? clasesSinVehiculo = null) =>
        Cubicabilidad != CubicabilidadRuta.Cubicable
        && !(ClaseViaje?.Trim() is { Length: > 0 } clase
             && (clasesSinVehiculo ?? clsCatalogosCaptura.ClasesSinVehiculoPorDefecto).Contains(clase, StringComparer.Ordinal));

    /// <summary>
    /// HU-032 (#197): las posiciones que pueden viajar a Nexo: todas, si todas traen sus tres medidas; ninguna si alguna es una mercancía
    /// sin medidas (no cubicable). Mandar solo una parte descuadraría el peso de la línea contra la suma de sus posiciones
    /// (<c>PESO_INCONSISTENTE</c> de Nexo, tolerancia 1 %): sin posiciones, a Nexo le basta el peso de la ruta.
    /// </summary>
    public IReadOnlyList<clsPosicionCarga> PosicionesParaNexo => Posiciones.All(p => p is not null && p.ConMedidas) ? Posiciones : [];

    /// <summary>
    /// Todos los errores de la línea (vacía si Nexo la aceptaría): obligatorios, largos, rangos con 2 decimales, posiciones y la
    /// regla de peso de <c>CapturarSolicitudValidador.ValidarLinea</c>. A diferencia de <see cref="clsSolicitudCosteo.Validar"/>,
    /// no se detiene en el primero: el reporte de una licitación trae todos.
    /// </summary>
    public IReadOnlyList<clsErrorCampo> Errores()
    {
        var errores = new List<clsErrorCampo>();

        // HU-021.6 (decisión 2): con el id de municipio el nombre no es obligatorio (se copia del catálogo de Nexo al resolverlo).
        if (OrigenMunicipioId is null)
        {
            Obligatorio(errores, "origen", Origen, clsSolicitudCosteo.LargoCiudad);
        }
        else
        {
            Largo(errores, "origen", Origen, clsSolicitudCosteo.LargoCiudad);
        }

        if (DestinoMunicipioId is null)
        {
            Obligatorio(errores, "destino", Destino, clsSolicitudCosteo.LargoCiudad);
        }
        else
        {
            Largo(errores, "destino", Destino, clsSolicitudCosteo.LargoCiudad);
        }

        IdMunicipio(errores, "origen_municipio_id", OrigenMunicipioId);
        IdMunicipio(errores, "destino_municipio_id", DestinoMunicipioId);
        IdMunicipio(errores, "ciudad_devolucion_municipio_id", CiudadDevolucionMunicipioId);
        IdMunicipio(errores, "ciudad_levante_municipio_id", CiudadLevanteMunicipioId);
        Largo(errores, "tipo_vehiculo", TipoVehiculo, clsSolicitudCosteo.LargoCatalogo);
        Largo(errores, "carroceria", Carroceria, clsSolicitudCosteo.LargoCatalogo);
        Largo(errores, "tipologia", Tipologia, clsSolicitudCosteo.LargoCorto);
        Largo(errores, "clase_viaje", ClaseViaje, clsSolicitudCosteo.LargoCorto);
        Largo(errores, "clase_contenedor", ClaseContenedor, clsSolicitudCosteo.LargoClaseContenedor);
        Largo(errores, "ciudad_devolucion_contenedor", CiudadDevolucionContenedor, clsSolicitudCosteo.LargoCiudad);
        Largo(errores, "ciudad_levante_contenedor", CiudadLevanteContenedor, clsSolicitudCosteo.LargoCiudad);
        Rango(errores, CampoValorLevante, ValorAprovisionamiento, 0m, clsSolicitudCosteo.MaximoValor);
        Rango(errores, "peso_kg", PesoKg, 0m, clsSolicitudCosteo.MaximoPeso);
        Rango(errores, "valor_mercancia", ValorMercancia, 0m, clsSolicitudCosteo.MaximoValor);
        Rango(errores, clsSolicitudCosteo.CampoPesoConTara, PesoConTaraKg, 0m, clsSolicitudCosteo.MaximoPeso);
        Rango(errores, "horas_cargue", HorasCargue, 0m, clsSolicitudCosteo.MaximoHoras);
        Rango(errores, "horas_descargue", HorasDescargue, 0m, clsSolicitudCosteo.MaximoHoras);
        errores.AddRange(ErroresCondicionales());

        if (Posiciones.Count > clsSolicitudCosteo.MaximoPosiciones)
        {
            errores.Add(new clsErrorCampo(CampoMercancias, clsCodigosLineaSolicitud.DemasiadasPosiciones,
                $"Una ruta admite máximo {clsSolicitudCosteo.MaximoPosiciones} mercancías."));
        }

        for (var i = 0; i < Posiciones.Count; i++)
        {
            // D6 (E2E 2026-10-16): el campo va en base 0 y con el nombre del contrato (mercancias[i].subcampo, como
            // mercancias[i].codigo_mercancia); el mensaje nombra la mercancía en base 1 ("Mercancía 2: ..."), sin rutas con índice.
            var posicion = Posiciones[i];
            var error = posicion is null
                ? new clsErrorPosicion(string.Empty, $"La mercancía {i + 1} está vacía.")
                : posicion.PrimerError(i + 1);
            if (error is not null)
            {
                var campo = error.Campo.Length == 0 ? $"{CampoMercancias}[{i}]" : $"{CampoMercancias}[{i}].{error.Campo}";
                errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.PosicionInvalida, error.Mensaje));
            }
        }

        var suma = SumaPesoPosiciones;
        var peso = PesoKg ?? (suma > 0 ? suma : null);
        if (peso is null or <= 0)
        {
            errores.Add(new clsErrorCampo("peso_kg", clsCodigosLineaSolicitud.PesoRequerido,
                "La línea debe traer el peso total en kg (o posiciones con peso)."));
        }
        else if (suma > 0 && Math.Abs(peso.Value - suma) / peso.Value > clsSolicitudCosteo.ToleranciaPeso)
        {
            errores.Add(new clsErrorCampo("peso_kg", clsCodigosLineaSolicitud.PesoInconsistente,
                $"El peso total ({peso.Value:0.##} kg) no coincide con la suma de las posiciones ({suma:0.##} kg)."));
        }

        return errores;
    }

    /// <summary>
    /// Validaciones condicionales de la captura (G3, HU-013.10/HU-013.11), las mismas para el formulario y para cada fila de una
    /// licitación: <c>clase_contenedor</c> solo con <c>clase_viaje = Contenedor</c> y obligatoria con ella, y
    /// <c>valor_aprovisionamiento</c> solo con una <c>ciudad_levante_contenedor</c> distinta del origen (Nexo lo descarta en otro caso).
    /// Los valores se comparan recortados, como se guardan.
    /// </summary>
    public IReadOnlyList<clsErrorCampo> ErroresCondicionales()
    {
        var errores = new List<clsErrorCampo>();
        var esContenedor = EsContenedor;
        var conClase = !string.IsNullOrWhiteSpace(ClaseContenedor);
        if (conClase && !esContenedor)
        {
            errores.Add(new clsErrorCampo("clase_contenedor", clsCodigosLineaSolicitud.CampoNoAplica,
                $"El campo 'clase_contenedor' solo aplica con clase_viaje = {clsCatalogosCaptura.ClaseViajeContenedor}."));
        }
        else if (esContenedor && !conClase)
        {
            errores.Add(new clsErrorCampo("clase_contenedor", clsCodigosLineaSolicitud.CampoRequerido,
                $"El campo 'clase_contenedor' es obligatorio con clase_viaje = {clsCatalogosCaptura.ClaseViajeContenedor}."));
        }

        if (ValorAprovisionamiento is not null && !LevanteDistintoDelOrigen)
        {
            // DEF-1027B-3 (#228): el campo del contrato (rutas[] y licitación), no el heredado que el cliente ya no envía.
            errores.Add(new clsErrorCampo(CampoValorLevante, clsCodigosLineaSolicitud.CampoNoAplica,
                $"El campo '{CampoValorLevante}' (antes '{CampoValorLevanteAnterior}') solo aplica con una ciudad_levante_contenedor distinta del origen."));
        }

        errores.AddRange(ErroresPesoConTara());
        return errores;
    }

    /// <summary>
    /// HU-030.1 (#184): <c>peso_con_tara_kg</c> solo con <c>clase_viaje = Contenedor</c> (<see cref="clsCodigosLineaSolicitud.CampoNoAplica"/>)
    /// y nunca menor que el peso de carga (<see cref="clsCodigosLineaSolicitud.PesoConTaraMenorQueCarga"/>). El tope (Nexo: hasta
    /// <see cref="clsSolicitudCosteo.MaximoPeso"/> kg con 2 decimales) lo revisa <see cref="Errores"/> con el rango de los demás pesos.
    /// </summary>
    private IEnumerable<clsErrorCampo> ErroresPesoConTara()
    {
        if (PesoConTaraKg is not { } conTara)
        {
            yield break;
        }

        if (!EsContenedor)
        {
            yield return new clsErrorCampo(clsSolicitudCosteo.CampoPesoConTara, clsCodigosLineaSolicitud.CampoNoAplica,
                $"El campo '{clsSolicitudCosteo.CampoPesoConTara}' (peso total con tara) solo aplica con clase_viaje = {clsCatalogosCaptura.ClaseViajeContenedor}.");
        }
        else if (PesoCarga is { } carga && conTara < carga)
        {
            yield return new clsErrorCampo(clsSolicitudCosteo.CampoPesoConTara, clsCodigosLineaSolicitud.PesoConTaraMenorQueCarga,
                $"El peso total con tara ({conTara:0.##} kg) no puede ser menor que el peso de la carga ({carga:0.##} kg): es la carga más la tara de los contenedores.");
        }
    }

    /// <summary>
    /// ¿El contenedor se levanta en una ciudad distinta del origen? Con los dos ids se comparan los ids; si no, los nombres
    /// recortados (sin distinguir mayúsculas). Una ciudad de levante que solo trae el id y un origen que solo trae el nombre (o al
    /// revés) cuentan como distintos: la comparación definitiva se hace con los ids ya resueltos.
    /// </summary>
    public bool LevanteDistintoDelOrigen
    {
        get
        {
            var levante = string.IsNullOrWhiteSpace(CiudadLevanteContenedor) ? null : CiudadLevanteContenedor.Trim();
            if (CiudadLevanteMunicipioId is null && levante is null)
            {
                return false;
            }

            if (CiudadLevanteMunicipioId is { } idLevante && OrigenMunicipioId is { } idOrigen)
            {
                return idLevante != idOrigen;
            }

            var origen = string.IsNullOrWhiteSpace(Origen) ? null : Origen.Trim();
            return levante is null || origen is null || !string.Equals(levante, origen, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reglas que solo aplican a una captura NUEVA (formulario o licitación, HU-021.6), después de resolver los municipios. No se aplican
    /// al revalidar lo guardado (rutas migradas o anteriores a estas reglas):
    /// <list type="bullet">
    /// <item>decisión 2: el origen y el destino llevan su id de municipio (<c>CAMPO_REQUERIDO</c> en <c>origen_municipio_id</c> /
    /// <c>destino_municipio_id</c>); una ciudad de devolución o de levante con nombre lleva también su id;</item>
    /// <item>el código de cada mercancía (<c>codigo_mercancia</c>, HU-023.3) NO se exige aquí: solo es obligatorio con el cubicador
    /// configurado y lo exige <c>clsCapturaRutasService</c> (sin cubicador no hay de dónde sacarlo y la ruta va al respaldo).</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<clsErrorCampo> ErroresCaptura()
    {
        var errores = new List<clsErrorCampo>();
        void Requerido(string campo, string mensaje) => errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.CampoRequerido, mensaje));

        if (OrigenMunicipioId is null)
        {
            Requerido("origen_municipio_id", "El origen debe llevar su id de municipio de Nexo (GET /web/municipios).");
        }

        if (DestinoMunicipioId is null)
        {
            Requerido("destino_municipio_id", "El destino debe llevar su id de municipio de Nexo (GET /web/municipios).");
        }

        if (CiudadDevolucionMunicipioId is null && !string.IsNullOrWhiteSpace(CiudadDevolucionContenedor))
        {
            Requerido("ciudad_devolucion_municipio_id", "La ciudad de devolución del contenedor debe llevar su id de municipio de Nexo.");
        }

        if (CiudadLevanteMunicipioId is null && !string.IsNullOrWhiteSpace(CiudadLevanteContenedor))
        {
            Requerido("ciudad_levante_municipio_id", "La ciudad de levante del contenedor debe llevar su id de municipio de Nexo.");
        }

        return errores;
    }

    private static void Obligatorio(List<clsErrorCampo> errores, string campo, string? valor, int maximo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.LineaIncompleta, $"El campo '{campo}' es obligatorio."));
            return;
        }

        Largo(errores, campo, valor, maximo);
    }

    internal static void Largo(List<clsErrorCampo> errores, string campo, string? valor, int maximo)
    {
        if (valor is not null && valor.Length > maximo)
        {
            errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo '{campo}' supera {maximo} caracteres."));
        }
    }

    private static void IdMunicipio(List<clsErrorCampo> errores, string campo, int? valor)
    {
        if (clsSolicitudCosteo.IdMunicipio(campo, valor) is { } error)
        {
            errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.ValorFueraDeRango, error.Mensaje));
        }
    }

    internal static void Rango(List<clsErrorCampo> errores, string campo, decimal? valor, decimal minimo, decimal maximo)
    {
        if (clsSolicitudCosteo.Rango(campo, valor, minimo, maximo) is { } error)
        {
            errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.ValorFueraDeRango, error.Mensaje));
        }
    }
}
