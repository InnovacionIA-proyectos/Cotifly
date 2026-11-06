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

/// <summary>
/// Cambios de carga que el asistente puede proponer al re-solicitar (solo campos sin dinero ni catálogos de Nexo). Sus límites y la
/// coherencia con las posiciones los valida el caso de uso de la re-solicitud (<c>IClsReSolicitudCosteoService.ValidarAsync</c>).
/// </summary>
/// <param name="PesoKg">Peso nuevo en kilogramos.</param>
/// <param name="HorasCargue">Horas de cargue.</param>
/// <param name="HorasDescargue">Horas de descargue.</param>
/// <param name="Exclusivo">Vehículo exclusivo.</param>
/// <param name="Consolidado">Carga consolidada.</param>
public sealed record clsCambiosCargaAsistente(
    decimal? PesoKg = null, decimal? HorasCargue = null, decimal? HorasDescargue = null, bool? Exclusivo = null, bool? Consolidado = null)
{
    public bool TieneCambios => PesoKg is not null || HorasCargue is not null || HorasDescargue is not null || Exclusivo is not null || Consolidado is not null;
}

/// <summary>
/// Arma las acciones propuestas del asistente (HU-013.6, #102, ADR-018 §2b) con código, nunca con el LLM. Las reglas son las MISMAS
/// del endpoint destino, sin copias: margen con <see cref="clsCotizacionSolicitud.ValidarAjusteMargen"/>; conexos con
/// <see cref="clsServiciosConexosCosteoService.Validar"/> y <see cref="clsServiciosConexosCosteoService.ValidarPrecondiciones"/>; la
/// re-solicitud la valida antes el caso de uso (<c>IClsReSolicitudCosteoService.ValidarAsync</c>, que necesita la base y el catálogo).
/// Arma el cuerpo exacto del contrato, pone el endpoint con el folio de la conversación y el <c>row_version</c> vigente, y redacta el
/// <c>resumen</c> con plantilla, con los textos del LLM (justificación, motivo) entre comillas para que el comercial vea lo que confirma. La vista previa del precio usa el mismo recálculo del ajuste
/// (<see cref="clsAjusteMargenCosteoService.Recalcular(clsCotizacionSolicitud, decimal)"/>,
/// sobre <see cref="clsPrecioMonedaCosteo"/>) SIN cambiar la cotización. Los errores no traen montos: su mensaje va al LLM.
/// </summary>
public static class clsPropuestasAsistente
{
    public const string MetodoPut = "PUT";
    public const string MetodoPost = "POST";

    /// <summary>Largo máximo de un texto del LLM citado en el resumen (justificación, motivo).</summary>
    public const int LargoMaximoCita = 90;

    public static string RutaMargen(string folio) => $"{Base(folio)}/costeo/margen";

    public static string RutaReSolicitud(string folio) => $"{Base(folio)}/costeo/re-solicitar";

    public static string RutaServiciosConexos(string folio) => $"{Base(folio)}/costeo/servicios-conexos";

    /// <summary>
    /// <c>ajustar_margen</c> → <c>PUT .../costeo/margen</c> <c>{margen, justificacion, linea_orden}</c> sobre la ruta
    /// <paramref name="lineaOrden"/> (HU-021.8, #135; sin ella, la única ruta). Errores: los de la ruta (<c>RUTA_REQUERIDA</c> con varias
    /// rutas y sin <paramref name="lineaOrden"/>, <c>RUTA_INEXISTENTE</c>), los del dominio (<c>AJUSTE_MARGEN_INVALIDO</c>,
    /// <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_ENVIADO</c>, <c>JUSTIFICACION_REQUERIDA</c> contra el sugerido de ESA ruta) y
    /// <c>AJUSTE_MARGEN_INVALIDO</c> si ya es el margen vigente de la ruta.
    /// </summary>
    public static clsResultado<clsAccionPropuesta> AjustarMargen(
        clsCotizacionSolicitud cotizacion, decimal margen, string? justificacion, int? lineaOrden = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var resuelta = cotizacion.ResolverRuta(lineaOrden, null);
        if (resuelta.EsFallo)
        {
            return resuelta.Error;
        }

        var ruta = resuelta.Valor;
        if (cotizacion.ValidarAjusteMargen(ruta.Orden, margen, justificacion) is { } invalido)
        {
            return invalido;
        }

        var nuevo = clsMargenAjuste.RedondearMargen(margen);
        var anterior = ruta.MargenAplicado;
        if (anterior == nuevo)
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, $"El margen ya es {Porcentaje(nuevo)}: no hay nada que ajustar.");
        }

        var recalculo = clsAjusteMargenCosteoService.Recalcular(cotizacion, ruta, nuevo);
        if (recalculo.EsFallo)
        {
            // El mensaje del módulo de precio puede nombrar valores: al LLM solo va el código y un texto fijo.
            return new clsErrorDominio(recalculo.Error.Codigo, "No se pudo calcular la vista previa del precio con ese margen. Pide al comercial que lo ajuste desde el detalle de la cotización.");
        }

        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;
        var conConexos = ruta.Conexos.Count > 0;
        var limpia = clsMargenAjuste.LimpiarJustificacion(justificacion);
        string resumen;
        if (cotizacion.Rutas.Count > 1)
        {
            // HU-021.8: solo cambia la ruta elegida; el total cambia por ella.
            resumen = $"Ajustar el margen de la ruta {ruta.Orden} ({ruta.Ruta}) de {Porcentaje(anterior)} a {Porcentaje(nuevo)}: su precio pasa de "
                      + $"{clsFormatoMoneda.Formatear(recalculo.Valor.PrecioRutaAnterior, moneda)} a {clsFormatoMoneda.Formatear(recalculo.Valor.PrecioRuta, moneda)} {moneda}"
                      + (recalculo.Valor.PrecioFinal is { } total
                          ? $" y el total, de {clsFormatoMoneda.Formatear(cotizacion.PrecioFinal, moneda)} a {clsFormatoMoneda.Formatear(total, moneda)} {moneda}."
                          : ".")
                      + " Las demás rutas no cambian.";
        }
        else
        {
            var concepto = conConexos ? "el precio total" : "el precio del flete";
            resumen = $"Ajustar el margen de {Porcentaje(anterior)} a {Porcentaje(nuevo)}: {concepto} pasa de "
                      + $"{clsFormatoMoneda.Formatear(cotizacion.PrecioFinal, moneda)} a {clsFormatoMoneda.Formatear(recalculo.Valor.PrecioFinal, moneda)} {moneda}.";
        }

        if (limpia is not null)
        {
            resumen += $" Justificación: {Cita(limpia)}.";
        }

        if (conConexos)
        {
            // HU-043.2 (#251): el margen es solo del transporte; los conexos van a su precio de venta y no cambian con el ajuste.
            resumen += " Los servicios conexos no cambian: se cotizan a su precio de venta, sin margen.";
        }

        resumen += SufijoPdf(cotizacion);
        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["margen"] = nuevo,
            ["justificacion"] = limpia,
            ["linea_orden"] = ruta.Orden,
        };
        return new clsAccionPropuesta(
            clsAccionPropuesta.TipoAjustarMargen, MetodoPut, RutaMargen(cotizacion.Folio), cuerpo, Recortar(resumen), clsCosteoCotizacionService.VersionEtag(cotizacion));
    }

    /// <summary>
    /// #278 (DU §5): Nexo recibe las horas de cargue y descargue en pareja. Si el asistente propone una sola, la otra se completa con la
    /// guardada en la ruta o, si la ruta no tiene, con el valor por defecto de Nexo (<see cref="clsLineasNegocioNexo.HorasPorDefectoDe"/>:
    /// 4, o 1 en una devolución de contenedor, según <paramref name="lineaNegocioNexo"/>, el <c>mapeoLineasNexo</c> de la línea de negocio de
    /// la cotización, como pide el DU: la línea de NEXO, no el nombre de la de Cotifly). Así el chat no
    /// falla con <c>CAMPO_REQUERIDO</c> en un cambio que antes funcionaba. Con las dos o ninguna, los cambios quedan como vienen.
    /// </summary>
    public static clsCambiosCargaAsistente CompletarHoras(clsCambiosCargaAsistente cambios, clsCotizacionSolicitud cotizacion, string? lineaNegocioNexo)
    {
        ArgumentNullException.ThrowIfNull(cambios);
        ArgumentNullException.ThrowIfNull(cotizacion);
        if ((cambios.HorasCargue is null) == (cambios.HorasDescargue is null))
        {
            return cambios;
        }

        var ruta = cotizacion.PrimeraRuta;
        var defecto = clsLineasNegocioNexo.HorasPorDefectoDe(lineaNegocioNexo);
        return cambios.HorasCargue is null
            ? cambios with { HorasCargue = ruta?.HorasCargue ?? defecto }
            : cambios with { HorasDescargue = ruta?.HorasDescargue ?? defecto };
    }

    /// <summary>
    /// Entrada del caso de uso de la re-solicitud con lo que propone el LLM: con cambios de carga siempre es versión nueva. Con ella se
    /// valida (<c>IClsReSolicitudCosteoService.ValidarAsync</c>) ANTES de <see cref="ReSolicitar"/>.
    /// </summary>
    public static clsEntradaReSolicitudCosteo EntradaReSolicitud(string? motivo, clsCambiosCargaAsistente cambios, bool nuevaVersion)
    {
        ArgumentNullException.ThrowIfNull(cambios);
        return new clsEntradaReSolicitudCosteo(motivo, cambios.TieneCambios ? CambiosDominio(cambios) : null, nuevaVersion);
    }

    /// <summary>
    /// <c>re_solicitar_costeo</c> → <c>POST .../costeo/re-solicitar</c> <c>{motivo, datos_carga?, nueva_version}</c>, para una
    /// <paramref name="entrada"/> que ya pasó la validación del caso de uso (motivo, estado, catálogo y reglas de la captura).
    /// </summary>
    public static clsAccionPropuesta ReSolicitar(clsCotizacionSolicitud cotizacion, clsEntradaReSolicitudCosteo entrada, clsCambiosCargaAsistente cambios)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(cambios);
        var motivo = (entrada.Motivo ?? string.Empty).Trim();
        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal) { ["motivo"] = motivo };
        if (DatosCarga(cambios) is { } datosCarga)
        {
            cuerpo["datos_carga"] = datosCarga;
        }

        cuerpo["nueva_version"] = entrada.VersionNueva;

        // HU-021.9 (#136; rehace el aviso de #124): el aviso de los conexos que se conservan tiene su espacio reservado dentro de los 300
        // caracteres: va justo después de la acción, se arma para que quepa completo ("y N más" incluido) y la descripción de los
        // cambios de carga se acorta antes que él. El motivo (texto del LLM) y el resto van después y son lo único que el recorte final
        // puede cortar.
        var inicio = entrada.VersionNueva
            ? $"Volver a pedir el costeo a Nexo en una versión nueva de {cotizacion.Folio}"
            : $"Volver a pedir el costeo a Nexo en el mismo folio {cotizacion.Folio}.";
        var minimoCambios = entrada.VersionNueva && cambios.TieneCambios ? DescripcionCambiosGenerica.Length : 1;
        var conexos = AvisoConexosConservados(clsReSolicitudCosteoService.ConexosConservados(cotizacion), entrada.VersionNueva,
            clsAccionPropuesta.LargoMaximoResumen - inicio.Length - minimoCambios - 1);
        var resumen = entrada.VersionNueva
            ? inicio
              // - 1: el recorte final deja "…" en el último carácter; así nunca cae sobre el aviso.
              + DescripcionCambios(cambios, clsAccionPropuesta.LargoMaximoResumen - inicio.Length - conexos.Length - 1)
              + conexos
              + $" Motivo: {Cita(motivo)}. La cotización original no cambia."
            : inicio + conexos
              + $" Motivo: {Cita(motivo)}. El precio actual se descarta hasta el nuevo costeo"
              + (cotizacion.PdfPath is null ? "." : " y el PDF actual queda desactualizado.");
        return new clsAccionPropuesta(
            clsAccionPropuesta.TipoReSolicitarCosteo, MetodoPost, RutaReSolicitud(cotizacion.Folio), cuerpo, Recortar(resumen), clsCosteoCotizacionService.VersionEtag(cotizacion));
    }

    /// <summary>
    /// <c>agregar_servicios_conexos</c> → <c>POST .../costeo/servicios-conexos</c> <c>{items: [{linea_orden, servicio_id, cantidad}]}</c>
    /// sobre la ruta <paramref name="lineaOrden"/> (sin ella, la única ruta; HU-021.8). Errores: los del endpoint antes de llamar a Nexo
    /// (<c>SOLICITUD_INVALIDA</c> por la forma, <c>ESTADO_NO_PERMITE</c>, <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_ENVIADO</c>,
    /// <c>RUTA_REQUERIDA</c>, <c>CONEXO_INVALIDO</c> y <c>LINEA_SIN_COTIZACION_NEXO</c>).
    /// </summary>
    public static clsResultado<clsAccionPropuesta> AgregarConexo(
        clsCotizacionSolicitud cotizacion, clsServicioConexoCatalogo servicio, decimal cantidad, int? lineaOrden)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(servicio);
        var items = clsServiciosConexosCosteoService.Validar([new clsConexoAgregarEntrada(lineaOrden, servicio.Id, cantidad)]);
        if (items.EsFallo)
        {
            return items.Error;
        }

        if (clsServiciosConexosCosteoService.ValidarPrecondiciones(cotizacion, items.Valor) is { } noPermitido)
        {
            return noPermitido;
        }

        var ruta = clsServiciosConexosCosteoService.RutaDe(cotizacion, items.Valor).Valor;

        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["linea_orden"] = ruta.Orden,
            ["servicio_id"] = items.Valor[0].ServicioId,
            ["cantidad"] = cantidad,
        };
        var cuerpo = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = new[] { item } };
        var unidad = string.IsNullOrWhiteSpace(servicio.Unidad) ? string.Empty : $" ({servicio.Unidad.Trim()})";
        var resumen = $"Agregar {Numero(cantidad)}{unidad} de «{servicio.Nombre ?? servicio.Codigo ?? servicio.Id}» a la ruta {ruta.Orden} en Nexo. "
                      // HU-043.2 (#251): el conexo no lleva el margen de la ruta.
                      + "Se cotiza a su precio de venta de Nexo, sin margen; queda incluido en el PDF."
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

    private static string SufijoPdf(clsCotizacionSolicitud cotizacion) =>
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

    /// <summary>Conexos que se nombran en el aviso del resumen; el resto se cuenta ("y N más").</summary>
    public const int MaximoConexosEnAviso = 3;

    /// <summary>Largo máximo del nombre de un conexo en el aviso del resumen.</summary>
    public const int LargoMaximoNombreConexo = 40;

    /// <summary>
    /// Aviso del resumen de <c>re_solicitar_costeo</c> con los conexos que se conservan (HU-021.9, #136; rehace el aviso de #124: con la
    /// decisión 3 los conexos son de Cotifly y siguen en el precio de su ruta), con un espacio inicial; vacío si no hay. Solo nombres (de
    /// Nexo, nunca del LLM, cada uno recortado a <see cref="LargoMaximoNombreConexo"/>) y sin montos. Siempre cabe completo en
    /// <paramref name="largoMaximo"/>: si no, nombra menos conexos (el resto va en "y N más") y, en el límite, solo los cuenta. Nunca
    /// pierde "y N más".
    /// </summary>
    public static string AvisoConexosConservados(IReadOnlyList<string> conexos, bool versionNueva, int largoMaximo = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(conexos);
        if (conexos.Count == 0)
        {
            return string.Empty;
        }

        string Aviso(int nombrados)
        {
            var resto = conexos.Count - nombrados;
            var nombres = nombrados == 0
                ? $"{conexos.Count} servicio(s) conexo(s)"
                : "los servicios conexos " + string.Join(", ", conexos.Take(nombrados).Select(n => "«" + NombreConexo(n) + "»"))
                  + (resto > 0 ? $" y {resto} más" : string.Empty);
            return versionNueva
                ? $" La versión nueva copia {nombres} con su costo y siguen en el precio."
                : $" Se conservan {nombres} con su costo y siguen en el precio.";
        }

        for (var nombrados = Math.Min(MaximoConexosEnAviso, conexos.Count); nombrados > 0; nombrados--)
        {
            var aviso = Aviso(nombrados);
            if (aviso.Length <= largoMaximo)
            {
                return aviso;
            }
        }

        return Aviso(0);
    }

    /// <summary>Nombre de un conexo para el modelo o el resumen: sin espacios repetidos y recortado a <see cref="LargoMaximoNombreConexo"/>.</summary>
    public static string NombreConexo(string nombre)
    {
        var limpio = string.Join(' ', (nombre ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return limpio.Length <= LargoMaximoNombreConexo ? limpio : limpio[..(LargoMaximoNombreConexo - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// " con estos cambios: …." de la versión nueva, acortada para caber en <paramref name="disponible"/> caracteres (el aviso de
    /// conexos tiene prioridad); "." si no hay cambios y " con cambios de carga." si no cabe ni un resumen corto.
    /// </summary>
    private static string DescripcionCambios(clsCambiosCargaAsistente cambios, int disponible)
    {
        if (!cambios.TieneCambios)
        {
            return ".";
        }

        const string Prefijo = " con estos cambios: ";
        var completa = $"{Prefijo}{DescribirCambios(cambios)}.";
        if (completa.Length <= disponible)
        {
            return completa;
        }

        var espacio = disponible - Prefijo.Length - 2;
        return espacio >= 15 ? $"{Prefijo}{DescribirCambios(cambios)[..espacio].TrimEnd()}…." : DescripcionCambiosGenerica;
    }

    /// <summary>Descripción mínima de los cambios de carga cuando la detallada no cabe junto al aviso de conexos.</summary>
    private const string DescripcionCambiosGenerica = " con cambios de carga.";

    /// <summary>Texto del LLM entre comillas latinas, recortado a <see cref="LargoMaximoCita"/> caracteres.</summary>
    public static string Cita(string? texto)
    {
        var limpio = string.Join(' ', (texto ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return "«" + (limpio.Length <= LargoMaximoCita ? limpio : limpio[..(LargoMaximoCita - 1)].TrimEnd() + "…") + "»";
    }

    private static string Recortar(string resumen) =>
        resumen.Length <= clsAccionPropuesta.LargoMaximoResumen ? resumen : resumen[..(clsAccionPropuesta.LargoMaximoResumen - 1)] + "…";
}
