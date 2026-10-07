using System.Globalization;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Cambios de carga que el asistente puede proponer al re-solicitar (solo campos sin dinero ni catálogos de Nexo).</summary>
/// <param name="PesoKg">Peso nuevo en kilogramos (0 a 999.999,99).</param>
/// <param name="HorasCargue">Horas de cargue (0 a 240).</param>
/// <param name="HorasDescargue">Horas de descargue (0 a 240).</param>
/// <param name="Exclusivo">Vehículo exclusivo.</param>
/// <param name="Consolidado">Carga consolidada.</param>
public sealed record clsCambiosCargaAsistente(
    decimal? PesoKg = null, decimal? HorasCargue = null, decimal? HorasDescargue = null, bool? Exclusivo = null, bool? Consolidado = null)
{
    public bool TieneCambios => PesoKg is not null || HorasCargue is not null || HorasDescargue is not null || Exclusivo is not null || Consolidado is not null;
}

/// <summary>
/// Arma las acciones propuestas del asistente (HU-013.6, #102, ADR-018 §2b) con código, nunca con el LLM: valida con las MISMAS reglas
/// del dominio que el endpoint destino (rango del margen, justificación, regla del PDF, estado del costeo, motivo, cantidades), arma el
/// cuerpo exacto del contrato, pone el endpoint con el folio de la conversación y el <c>row_version</c> vigente, y redacta el
/// <c>resumen</c> con plantilla. La vista previa del precio usa el mismo recálculo del ajuste
/// (<see cref="clsAjusteMargenCosteoService.Recalcular(IClsPrecioService, clsCotizacion, IReadOnlyList{clsCotizacionLineaSolicitud}, decimal)"/>,
/// sobre <see cref="clsPrecioMonedaCosteo"/>) SIN cambiar la cotización. Los errores no traen montos: su mensaje va al LLM.
/// </summary>
public static class clsPropuestasAsistente
{
    public const string MetodoPut = "PUT";
    public const string MetodoPost = "POST";

    /// <summary>Peso máximo de <c>DatosCargaWeb.peso_kg</c>.</summary>
    public const decimal PesoMaximo = 999_999.99m;

    /// <summary>Horas máximas de cargue o descargue (<c>DatosCargaWeb</c>).</summary>
    public const decimal HorasMaximas = 240m;

    public static string RutaMargen(string folio) => $"{Base(folio)}/costeo/margen";

    public static string RutaReSolicitud(string folio) => $"{Base(folio)}/costeo/re-solicitar";

    public static string RutaServiciosConexos(string folio) => $"{Base(folio)}/costeo/servicios-conexos";

    /// <summary>
    /// <c>ajustar_margen</c> → <c>PUT .../costeo/margen</c> <c>{margen, justificacion}</c>. Errores: los del dominio
    /// (<c>AJUSTE_MARGEN_INVALIDO</c>, <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_ENVIADO</c>, <c>JUSTIFICACION_REQUERIDA</c>) y
    /// <c>AJUSTE_MARGEN_INVALIDO</c> si ya es el margen vigente.
    /// </summary>
    public static clsResultado<clsAccionPropuesta> AjustarMargen(
        IClsPrecioService precios, clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, decimal margen, string? justificacion)
    {
        ArgumentNullException.ThrowIfNull(precios);
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        if (cotizacion.ValidarAjusteMargen(margen, justificacion) is { } invalido)
        {
            return invalido;
        }

        var nuevo = clsMargenAjuste.RedondearMargen(margen);
        var anterior = cotizacion.MargenAplicado;
        if (anterior == nuevo)
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, $"El margen ya es {Porcentaje(nuevo)}: no hay nada que ajustar.");
        }

        var recalculo = clsAjusteMargenCosteoService.Recalcular(precios, cotizacion, lineas, nuevo);
        if (recalculo.EsFallo)
        {
            // El mensaje del módulo de precio puede nombrar valores: al LLM solo va el código y un texto fijo.
            return new clsErrorDominio(recalculo.Error.Codigo, "No se pudo calcular la vista previa del precio con ese margen. Pide al comercial que lo ajuste desde el detalle de la cotización.");
        }

        var moneda = cotizacion.Moneda ?? clsCotizacion.MonedaPesos;
        var concepto = lineas.Count > 1 ? "el precio total" : "el precio del flete";
        var resumen = $"Ajustar el margen de {Porcentaje(anterior)} a {Porcentaje(nuevo)}: {concepto} pasa de "
                      + $"{clsFormatoMoneda.Formatear(cotizacion.PrecioFinal, moneda)} a {clsFormatoMoneda.Formatear(recalculo.Valor.PrecioFinal, moneda)} {moneda}.";
        if (clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Count > 0)
        {
            resumen += " Los servicios conexos se recalculan con el mismo margen.";
        }

        resumen += SufijoPdf(cotizacion);
        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["margen"] = nuevo,
            ["justificacion"] = clsMargenAjuste.LimpiarJustificacion(justificacion),
        };
        return new clsAccionPropuesta(
            clsAccionPropuesta.TipoAjustarMargen, MetodoPut, RutaMargen(cotizacion.Folio), cuerpo, Recortar(resumen), clsCosteoCotizacionService.VersionEtag(cotizacion));
    }

    /// <summary>
    /// <c>re_solicitar_costeo</c> → <c>POST .../costeo/re-solicitar</c> <c>{motivo, datos_carga?, nueva_version}</c>. Con cambios de carga
    /// siempre es versión nueva. Errores: <c>SOLICITUD_COSTEO_INVALIDA</c> (motivo, límites), <c>ESTADO_NO_PERMITE</c> y
    /// <c>PDF_YA_ENVIADO</c> (estado de la cotización, las mismas reglas del endpoint).
    /// </summary>
    public static clsResultado<clsAccionPropuesta> ReSolicitar(
        clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string? motivo, clsCambiosCargaAsistente cambios, bool nuevaVersion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(cambios);
        if (cambios.PesoKg is < 0m or > PesoMaximo
            || cambios.HorasCargue is < 0m or > HorasMaximas
            || cambios.HorasDescargue is < 0m or > HorasMaximas)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"Revisa los cambios de carga: el peso va de 0 a {Numero(PesoMaximo)} kg y las horas de cargue o descargue de 0 a {Numero(HorasMaximas)}.");
        }

        var datosCarga = DatosCarga(cambios);
        var entrada = new clsEntradaReSolicitudCosteo(motivo, datosCarga is null ? null : CambiosDominio(cambios), nuevaVersion);
        if (clsReSolicitudCosteoService.ValidarEntrada(entrada) is { } invalida)
        {
            return invalida;
        }

        if (cotizacion.ValidarReSolicitud(entrada.VersionNueva) is { } noPermitida)
        {
            return noPermitida;
        }

        if (datosCarga is not null && clsCosteoCotizacionService.EsPliego(cotizacion, lineas))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La cotización {cotizacion.Folio} es una licitación: los cambios de carga se hacen desde el detalle, línea por línea.");
        }

        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal) { ["motivo"] = motivo!.Trim() };
        if (datosCarga is not null)
        {
            cuerpo["datos_carga"] = datosCarga;
        }

        cuerpo["nueva_version"] = entrada.VersionNueva;
        var resumen = entrada.VersionNueva
            ? $"Volver a pedir el costeo a Nexo en una versión nueva de {cotizacion.Folio}"
              + (datosCarga is null ? "." : $" con estos cambios: {DescribirCambios(cambios)}.")
              + " La cotización original no cambia."
            : $"Volver a pedir el costeo a Nexo en el mismo folio {cotizacion.Folio}. El precio actual se descarta hasta el nuevo costeo"
              + (cotizacion.PdfPath is null ? "." : " y el PDF actual queda desactualizado.");
        return new clsAccionPropuesta(
            clsAccionPropuesta.TipoReSolicitarCosteo, MetodoPost, RutaReSolicitud(cotizacion.Folio), cuerpo, Recortar(resumen), clsCosteoCotizacionService.VersionEtag(cotizacion));
    }

    /// <summary>
    /// <c>agregar_servicios_conexos</c> → <c>POST .../costeo/servicios-conexos</c> <c>{items: [{linea_orden, servicio_id, cantidad}]}</c>.
    /// Errores: <c>CONEXO_INVALIDO</c> (cantidad o línea), <c>ESTADO_NO_PERMITE</c> (licitación), <c>COSTEO_NO_LISTO</c> y
    /// <c>PDF_YA_ENVIADO</c>, con las mismas reglas del endpoint.
    /// </summary>
    public static clsResultado<clsAccionPropuesta> AgregarConexo(
        clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, clsServicioConexoCatalogo servicio, decimal cantidad, int lineaOrden)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(servicio);
        if (cantidad < clsServiciosConexosCosteoService.CantidadMinima || cantidad > clsServiciosConexosCosteoService.CantidadMaxima
            || decimal.Round(cantidad, 2) != cantidad)
        {
            return new clsErrorDominio(clsCodigosError.ConexoInvalido,
                $"La cantidad va de {Numero(clsServiciosConexosCosteoService.CantidadMinima)} a {Numero(clsServiciosConexosCosteoService.CantidadMaxima)}, con hasta 2 decimales.");
        }

        if (clsCosteoCotizacionService.EsPliego(cotizacion, lineas))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La cotización {cotizacion.Folio} es una licitación: no admite servicios conexos en esta iteración.");
        }

        if (!cotizacion.TieneCostoDisponible || cotizacion.MargenAplicado is null || cotizacion.MetodoCalculo is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado: espera el costeo para agregar conexos.");
        }

        if (cotizacion.ValidarCambioEtapaFinal() is { } entregada)
        {
            return entregada;
        }

        if (lineaOrden < 1 || (lineas.Count == 0 ? lineaOrden != 1 : lineas.All(l => l.Orden != lineaOrden)))
        {
            return new clsErrorDominio(clsCodigosError.ConexoInvalido, $"La línea {lineaOrden} no existe en la cotización {cotizacion.Folio}.");
        }

        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["linea_orden"] = lineaOrden,
            ["servicio_id"] = servicio.Id,
            ["cantidad"] = cantidad,
        };
        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = new[] { item } };
        var unidad = string.IsNullOrWhiteSpace(servicio.Unidad) ? string.Empty : $" ({servicio.Unidad.Trim()})";
        var resumen = $"Agregar {Numero(cantidad)}{unidad} de «{servicio.Nombre ?? servicio.Codigo ?? servicio.Id}» a la línea {lineaOrden} en Nexo. "
                      + $"Nexo calcula su valor y Cotifly le aplica el margen de {Porcentaje(cotizacion.MargenAplicado)}; queda incluido en el PDF."
                      + SufijoPdf(cotizacion);
        return new clsAccionPropuesta(
            clsAccionPropuesta.TipoAgregarServiciosConexos, MetodoPost, RutaServiciosConexos(cotizacion.Folio), cuerpo, Recortar(resumen),
            clsCosteoCotizacionService.VersionEtag(cotizacion));
    }

    /// <summary>Margen como porcentaje legible: <c>0.18</c> → <c>18 %</c>, <c>0.125</c> → <c>12,5 %</c>.</summary>
    public static string Porcentaje(decimal? fraccion) => fraccion is { } valor
        ? Math.Round(valor * 100m, 2, MidpointRounding.ToEven).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + " %"
        : "(sin margen)";

    /// <summary>Número con miles en punto y decimales en coma: <c>28000</c> → <c>28.000</c>, <c>0.5</c> → <c>0,5</c>.</summary>
    public static string Numero(decimal valor) =>
        valor.ToString("#,0.##", CultureInfo.InvariantCulture).Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.');

    private static string Base(string folio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        return $"/web/cotizaciones/{Uri.EscapeDataString(folio.Trim())}";
    }

    private static string SufijoPdf(clsCotizacion cotizacion) =>
        cotizacion.PdfPath is null ? string.Empty : " El PDF actual queda desactualizado y se debe regenerar.";

    private static Dictionary<string, object?>? DatosCarga(clsCambiosCargaAsistente cambios)
    {
        if (!cambios.TieneCambios)
        {
            return null;
        }

        var datos = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cambios.PesoKg is { } peso)
        {
            datos[clsCambiosCarga.CampoPesoKg] = peso;
        }

        if (cambios.HorasCargue is { } cargue)
        {
            datos[clsCambiosCarga.CampoHorasCargue] = cargue;
        }

        if (cambios.HorasDescargue is { } descargue)
        {
            datos[clsCambiosCarga.CampoHorasDescargue] = descargue;
        }

        if (cambios.Exclusivo is { } exclusivo)
        {
            datos[clsCambiosCarga.CampoExclusivo] = exclusivo;
        }

        if (cambios.Consolidado is { } consolidado)
        {
            datos[clsCambiosCarga.CampoConsolidado] = consolidado;
        }

        return datos;
    }

    private static clsCambiosCarga CambiosDominio(clsCambiosCargaAsistente cambios) => new()
    {
        Presentes = DatosCarga(cambios)!.Keys.ToHashSet(StringComparer.Ordinal),
        PesoKg = cambios.PesoKg,
        HorasCargue = cambios.HorasCargue,
        HorasDescargue = cambios.HorasDescargue,
        Exclusivo = cambios.Exclusivo,
        Consolidado = cambios.Consolidado,
    };

    private static string DescribirCambios(clsCambiosCargaAsistente cambios)
    {
        var partes = new List<string>();
        if (cambios.PesoKg is { } peso)
        {
            partes.Add($"peso {Numero(peso)} kg");
        }

        if (cambios.HorasCargue is { } cargue)
        {
            partes.Add($"{Numero(cargue)} h de cargue");
        }

        if (cambios.HorasDescargue is { } descargue)
        {
            partes.Add($"{Numero(descargue)} h de descargue");
        }

        if (cambios.Exclusivo is { } exclusivo)
        {
            partes.Add(exclusivo ? "vehículo exclusivo" : "vehículo no exclusivo");
        }

        if (cambios.Consolidado is { } consolidado)
        {
            partes.Add(consolidado ? "carga consolidada" : "carga no consolidada");
        }

        return string.Join(", ", partes);
    }

    private static string Recortar(string resumen) =>
        resumen.Length <= clsAccionPropuesta.LargoMaximoResumen ? resumen : resumen[..(clsAccionPropuesta.LargoMaximoResumen - 1)] + "…";
}
