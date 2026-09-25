using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Pdf.Validators;

/// <summary>
/// Validación del payload del PDF (<c>PDF_PAYLOAD_INVALIDO</c>). Exige la línea de negocio (con ella se resuelve
/// <c>lineaNegocioId</c>) y al menos una fila de tarifa, y pone largos máximos a cada texto (medidos después de
/// <see cref="clsTextoSeguro"/>) para que un payload desbordado no produzca un PDF de decenas de páginas. El Python no
/// validaba nada: un payload sin tarifa generaba una tabla vacía.
/// </summary>
public static class clsValidadorCotizacionPdfPayload
{
    public const int MaxTextoCorto = 300;
    public const int MaxLinea = 100;
    public const int MaxNota = 2000;
    public const int MaxParrafo = 5000;
    public const int MaxElementosLista = 30;
    public const int MaxContactos = 20;
    public const int MaxSecciones = 20;
    public const int MaxFilas = 60;

    /// <summary>Primer problema del payload, o <c>null</c> si es válido.</summary>
    public static clsErrorDominio? Validar(clsCotizacionPdfPayload? payload)
    {
        if (payload is null)
        {
            return Error("Falta el payload de la cotización.");
        }

        if (clsTextoSeguro.LimpiarLinea(payload.LineaNegocio) is null)
        {
            return Error("Falta 'linea_negocio'.");
        }

        var metodo = clsTextoSeguro.LimpiarLinea(payload.MetodoCalculo)?.ToLowerInvariant();
        if (metodo is not (null or clsReglaMargen.MetodoMargen or clsReglaMargen.MetodoMarkup))
        {
            return Error($"'metodo_calculo' debe ser 'margen' o 'markup' (llegó '{metodo}').");
        }

        var secciones = SeccionesDe(payload);
        if (secciones.Count > MaxSecciones)
        {
            return Error($"'secciones' admite máximo {MaxSecciones} tablas.");
        }

        var filas = secciones.Sum(s => s.Filas?.Count ?? 0);
        if (filas == 0)
        {
            return Error("La cotización no tiene filas de tarifa ('tarifa.filas' o 'secciones[].filas').");
        }

        if (filas > MaxFilas)
        {
            return Error($"La cotización admite máximo {MaxFilas} filas de tarifa.");
        }

        return clsValidacionTextos.Primero(
            Largo("linea_negocio", payload.LineaNegocio, MaxLinea),
            Largo("cliente", payload.Cliente, MaxTextoCorto),
            Largo("ciudad", payload.Ciudad, MaxLinea),
            Largo("fecha", payload.Fecha, 30),
            Largo("modalidad", payload.Modalidad, MaxLinea),
            Largo("asunto", payload.Asunto, MaxTextoCorto),
            Lista("contactos", payload.Contactos, MaxContactos, MaxTextoCorto),
            Largo("comercial.cargo", payload.Comercial?.Cargo, MaxLinea),
            Largo("nota_decreto", payload.NotaDecreto, MaxNota),
            Largo("dias_libres", payload.DiasLibres, MaxNota),
            Largo("frase_final", payload.FraseFinal, MaxTextoCorto),
            Largo("aduana.modalidad", payload.Aduana?.Modalidad, MaxTextoCorto),
            Largo("aduana.frontera", payload.Aduana?.Frontera, MaxTextoCorto),
            Largo("aduana.arancel", payload.Aduana?.Arancel, MaxTextoCorto),
            Largo("extradimensionada.peso_kg", payload.Extradimensionada?.PesoKg, MaxTextoCorto),
            Largo("extradimensionada.ancho", payload.Extradimensionada?.Ancho, MaxTextoCorto),
            Largo("extradimensionada.largo", payload.Extradimensionada?.Largo, MaxTextoCorto),
            Largo("extradimensionada.fotos", payload.Extradimensionada?.Fotos, MaxTextoCorto),
            Largo("refrigerada.temperatura", payload.Refrigerada?.Temperatura, MaxTextoCorto),
            ValidarTextos(payload.Textos),
            ValidarServicios(payload.ServiciosConexos),
            secciones.Select((s, i) => ValidarSeccion(s, payload.Secciones is { Count: > 0 } ? $"secciones[{i}]" : "tarifa"))
                .FirstOrDefault(e => e is not null));
    }

    /// <summary>Tablas del payload: <c>secciones</c> si trae, si no <c>tarifa</c> (misma regla que <c>generador.py</c>).</summary>
    public static IReadOnlyList<clsTarifaPayload> SeccionesDe(clsCotizacionPdfPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Secciones is { Count: > 0 } secciones)
        {
            return [.. secciones.Where(s => s is not null)];
        }

        return payload.Tarifa is null ? [] : [payload.Tarifa];
    }

    private static clsErrorDominio? ValidarSeccion(clsTarifaPayload seccion, string ruta)
    {
        var error = clsValidacionTextos.Primero(
            Largo($"{ruta}.titulo", seccion.Titulo, MaxTextoCorto),
            Largo($"{ruta}.origen", seccion.Origen, MaxTextoCorto),
            Largo($"{ruta}.nota_general", seccion.NotaGeneral, MaxNota),
            Lista($"{ruta}.incluye", seccion.Incluye, MaxElementosLista, MaxTextoCorto),
            Lista($"{ruta}.no_incluye", seccion.NoIncluye, MaxElementosLista, MaxTextoCorto));
        if (error is not null)
        {
            return error;
        }

        foreach (var (fila, i) in (seccion.Filas ?? []).Select((f, i) => (f, i)))
        {
            if (fila is null)
            {
                return Error($"{ruta}.filas[{i}] está vacía.");
            }

            if (fila.Tarifa is < 0m || fila.Costo is < 0m || fila.Margen is < 0m)
            {
                return Error($"{ruta}.filas[{i}]: la tarifa, el costo y el margen no pueden ser negativos.");
            }

            error = clsValidacionTextos.Primero(
                Largo($"{ruta}.filas[{i}].destino", fila.Destino, MaxTextoCorto),
                Largo($"{ruta}.filas[{i}].vehiculo_peso", fila.VehiculoPeso, MaxTextoCorto),
                Largo($"{ruta}.filas[{i}].nota", fila.Nota, MaxNota),
                Largo($"{ruta}.filas[{i}].moneda", fila.Moneda, 10));
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static clsErrorDominio? ValidarTextos(clsTextosPayload? textos) =>
        textos is null
            ? null
            : clsValidacionTextos.Primero(
                Lista("textos.intro", textos.Intro, MaxElementosLista, MaxParrafo),
                Lista("textos.incluye", textos.Incluye, MaxElementosLista, MaxTextoCorto),
                Lista("textos.no_incluye", textos.NoIncluye, MaxElementosLista, MaxTextoCorto),
                Largo("textos.nota_general", textos.NotaGeneral, MaxParrafo),
                Largo("textos.seguro", textos.Seguro, MaxParrafo),
                Largo("textos.stand_by", textos.StandBy, MaxParrafo),
                Largo("textos.vigencia", textos.Vigencia, MaxParrafo),
                Largo("textos.facturacion", textos.Facturacion, MaxParrafo));

    private static clsErrorDominio? ValidarServicios(List<clsServicioConexoPayload>? servicios)
    {
        if (servicios is null)
        {
            return null;
        }

        if (servicios.Count > MaxElementosLista)
        {
            return Error($"'servicios_conexos' admite máximo {MaxElementosLista} servicios.");
        }

        foreach (var (servicio, i) in servicios.Select((s, i) => (s, i)))
        {
            if (clsTextoSeguro.LimpiarLinea(servicio?.Descripcion) is null)
            {
                return Error($"'servicios_conexos[{i}].descripcion' es obligatoria.");
            }

            if (servicio!.Precio is < 0m)
            {
                return Error($"'servicios_conexos[{i}].precio' no puede ser negativo.");
            }

            var error = Largo($"servicios_conexos[{i}].descripcion", servicio.Descripcion, MaxTextoCorto)
                        ?? Largo($"servicios_conexos[{i}].moneda", servicio.Moneda, 10);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static clsErrorDominio? Largo(string campo, string? valor, int maximo) =>
        clsTextoSeguro.Limpiar(valor) is { Length: var largo } && largo > maximo
            ? Error($"'{campo}' supera {maximo} caracteres.")
            : null;

    private static clsErrorDominio? Lista(string campo, List<string>? valores, int maxElementos, int maxLargo)
    {
        if (valores is null)
        {
            return null;
        }

        if (valores.Count > maxElementos)
        {
            return Error($"'{campo}' admite máximo {maxElementos} elementos.");
        }

        return valores.Select((v, i) => Largo($"{campo}[{i}]", v, maxLargo)).FirstOrDefault(e => e is not null);
    }

    private static clsErrorDominio Error(string mensaje) => new(clsCodigosError.PdfPayloadInvalido, mensaje);

    private static class clsValidacionTextos
    {
        public static clsErrorDominio? Primero(params clsErrorDominio?[] errores) => errores.FirstOrDefault(e => e is not null);
    }
}
