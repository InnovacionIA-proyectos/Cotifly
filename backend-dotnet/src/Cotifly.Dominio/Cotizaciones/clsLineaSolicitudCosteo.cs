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
    public string? CiudadDevolucionContenedor { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>
    /// Horas de cargue propias de la línea (<c>horasCargue</c> de <c>CapturaLineaRequest</c>, G5 de HU-013.11); <c>null</c> = las del
    /// encabezado. Solo las licitaciones las traen por fila; el formulario de una línea usa las del encabezado.
    /// </summary>
    public decimal? HorasCargue { get; init; }

    /// <summary>Horas de descargue propias de la línea (<c>horasDescargue</c>); <c>null</c> = las del encabezado.</summary>
    public decimal? HorasDescargue { get; init; }

    /// <summary>Posiciones de cubicaje (bultos), en el orden del formulario o del pliego.</summary>
    public IReadOnlyList<clsPosicionCarga> Posiciones { get; init; } = [];

    /// <summary>
    /// Ruta de Cotifly de la que sale la línea (HU-021.7, #134; petición N1 a Nexo): solo la llena <see cref="clsLineaSolicitud.ALineaNexo"/>
    /// al armar una línea por camión. No la captura el comercial ni se valida. Viaja a Nexo como <c>referenciaRuta</c> solo cuando Nexo
    /// la acepte (<c>Nexo:EnviarReferenciaRuta</c>); mientras tanto, el emparejamiento usa el contenido de la línea y el orden de envío.
    /// </summary>
    public Guid? RutaId { get; init; }

    /// <summary>Suma <c>cantidad × pesoKg</c> de las posiciones con peso (la misma de Nexo).</summary>
    public decimal SumaPesoPosiciones => Posiciones.Where(p => p?.PesoKg is > 0).Sum(p => p.Cantidad * p.PesoKg!.Value);

    /// <summary>
    /// Todos los errores de la línea (vacía si Nexo la aceptaría): obligatorios, largos, rangos con 2 decimales, posiciones y la
    /// regla de peso de <c>CapturarSolicitudValidador.ValidarLinea</c>. A diferencia de <see cref="clsSolicitudCosteo.Validar"/>,
    /// no se detiene en el primero: el reporte de una licitación trae todos.
    /// </summary>
    public IReadOnlyList<clsErrorCampo> Errores()
    {
        var errores = new List<clsErrorCampo>();
        Obligatorio(errores, "origen", Origen, clsSolicitudCosteo.LargoCiudad);
        Obligatorio(errores, "destino", Destino, clsSolicitudCosteo.LargoCiudad);
        IdMunicipio(errores, "origen_municipio_id", OrigenMunicipioId);
        IdMunicipio(errores, "destino_municipio_id", DestinoMunicipioId);
        Largo(errores, "tipo_vehiculo", TipoVehiculo, clsSolicitudCosteo.LargoCatalogo);
        Largo(errores, "carroceria", Carroceria, clsSolicitudCosteo.LargoCatalogo);
        Largo(errores, "tipologia", Tipologia, clsSolicitudCosteo.LargoCorto);
        Largo(errores, "clase_viaje", ClaseViaje, clsSolicitudCosteo.LargoCorto);
        Largo(errores, "clase_contenedor", ClaseContenedor, clsSolicitudCosteo.LargoClaseContenedor);
        Largo(errores, "ciudad_devolucion_contenedor", CiudadDevolucionContenedor, clsSolicitudCosteo.LargoCiudad);
        Largo(errores, "ciudad_levante_contenedor", CiudadLevanteContenedor, clsSolicitudCosteo.LargoCiudad);
        Rango(errores, "valor_aprovisionamiento", ValorAprovisionamiento, 0m, clsSolicitudCosteo.MaximoValor);
        Rango(errores, "peso_kg", PesoKg, 0m, clsSolicitudCosteo.MaximoPeso);
        Rango(errores, "valor_mercancia", ValorMercancia, 0m, clsSolicitudCosteo.MaximoValor);
        Rango(errores, "horas_cargue", HorasCargue, 0m, clsSolicitudCosteo.MaximoHoras);
        Rango(errores, "horas_descargue", HorasDescargue, 0m, clsSolicitudCosteo.MaximoHoras);
        errores.AddRange(ErroresCondicionales());

        if (Posiciones.Count > clsSolicitudCosteo.MaximoPosiciones)
        {
            errores.Add(new clsErrorCampo("posiciones", clsCodigosLineaSolicitud.DemasiadasPosiciones,
                $"Una línea admite máximo {clsSolicitudCosteo.MaximoPosiciones} posiciones."));
        }

        for (var i = 0; i < Posiciones.Count; i++)
        {
            var posicion = Posiciones[i];
            var error = posicion is null
                ? new clsErrorDominio(clsSolicitudCosteo.CodigoError, $"La posición {i + 1} está vacía.")
                : posicion.Validar(i + 1);
            if (error is not null)
            {
                errores.Add(new clsErrorCampo($"posiciones[{i}]", clsCodigosLineaSolicitud.PosicionInvalida, error.Mensaje));
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
        var esContenedor = string.Equals(ClaseViaje?.Trim(), clsCatalogosCaptura.ClaseViajeContenedor, StringComparison.Ordinal);
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

        var levanteDistinto = !string.IsNullOrWhiteSpace(CiudadLevanteContenedor)
            && !string.Equals(CiudadLevanteContenedor.Trim(), Origen?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (ValorAprovisionamiento is not null && !levanteDistinto)
        {
            errores.Add(new clsErrorCampo("valor_aprovisionamiento", clsCodigosLineaSolicitud.CampoNoAplica,
                "El campo 'valor_aprovisionamiento' solo aplica con una ciudad_levante_contenedor distinta del origen."));
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
