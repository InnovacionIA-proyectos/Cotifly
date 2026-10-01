using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Códigos por campo del reporte de validación de una licitación (HU-011.2, <c>docs/HU-011-formato-excel-licitacion.md</c> §5).
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
    public decimal? ValorEstimado { get; init; }
    public string? Referencia { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary>Todos los errores del encabezado (vacía si Nexo lo aceptaría).</summary>
    public IReadOnlyList<clsErrorCampo> Errores()
    {
        var errores = new List<clsErrorCampo>();
        clsLineaSolicitudCosteo.Largo(errores, "referencia", Referencia, clsSolicitudCosteo.LargoReferencia);
        clsLineaSolicitudCosteo.Rango(errores, "valor_estimado", ValorEstimado, 0m, clsSolicitudCosteo.MaximoValor);
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

    /// <summary>Posiciones de cubicaje (bultos), en el orden del formulario o del pliego.</summary>
    public IReadOnlyList<clsPosicionCarga> Posiciones { get; init; } = [];

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

    internal static void Rango(List<clsErrorCampo> errores, string campo, decimal? valor, decimal minimo, decimal maximo)
    {
        if (clsSolicitudCosteo.Rango(campo, valor, minimo, maximo) is { } error)
        {
            errores.Add(new clsErrorCampo(campo, clsCodigosLineaSolicitud.ValorFueraDeRango, error.Mensaje));
        }
    }
}
