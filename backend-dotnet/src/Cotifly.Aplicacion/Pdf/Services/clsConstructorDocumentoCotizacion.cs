using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Arma el <see cref="clsDocumentoCotizacion"/> a partir del payload con las mismas reglas que <c>construir_html</c> de
/// <c>armar_cotizacion.py</c> y <c>armar_multi.py</c>: textos del payload o estándar, tarifa de cada fila (<c>tarifa</c>
/// o costo y margen con <c>metodo_calculo</c>), bloques condicionales (aduana solo si la línea empieza por
/// "internacional", extradimensionada, refrigerada), imagen y leyenda por línea, y firma con los datos reales.
/// Función pura: no lee la base ni el reloj.
/// </summary>
public static class clsConstructorDocumentoCotizacion
{
    /// <summary>Construye el documento. Supone el payload ya validado con <see cref="clsValidadorCotizacionPdfPayload"/>.</summary>
    /// <param name="payload">Payload del PDF.</param>
    /// <param name="folio">Folio que se imprime como referencia.</param>
    /// <param name="firma">Firma con los datos de <c>UsuariosWeb</c> (el celular solo si hay teléfono).</param>
    /// <param name="hoy">Fecha de negocio para cuando el payload no trae <c>fecha</c>.</param>
    /// <param name="monedaPorDefecto">
    /// Moneda de la cotización (obligatoria: <c>COP</c> o <c>USD</c>) para las filas y servicios sin moneda propia (<c>USD</c> → <c>US$</c>).
    /// </param>
    /// <param name="trmAplicada">
    /// TRM aplicada en la cotización (HU-012.3): 0 en COP; en USD debe ser &gt; 0 (si no, <c>PDF_PAYLOAD_INVALIDO</c>: no hay un PDF en dólares
    /// sin la TRM de la nota) y agrega la nota de TRM mínima (<see cref="clsTextosEstandar.NotaTrmMinima"/>). Es la persistida con la
    /// cotización, nunca la del día en que se genera el PDF.
    /// </param>
    public static clsResultado<clsDocumentoCotizacion> Construir(
        clsCotizacionPdfPayload payload, string folio, clsFirmaDocumento firma, DateOnly hoy, string monedaPorDefecto, decimal trmAplicada)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        ArgumentNullException.ThrowIfNull(firma);
        ArgumentException.ThrowIfNullOrWhiteSpace(monedaPorDefecto);
        var enDolares = !clsConversorMoneda.EsPesos(monedaPorDefecto);
        if (enDolares && trmAplicada <= 0m)
        {
            return new clsErrorDominio(
                clsCodigosError.PdfPayloadInvalido,
                $"La cotización en {monedaPorDefecto.Trim().ToUpperInvariant()} no trae la TRM aplicada: sin ella no se puede imprimir la nota de TRM mínima (HU-012.3).");
        }

        var esMulti = payload.Secciones is { Count: > 0 };
        var textos = esMulti ? clsTextosEstandar.MultiSeccion : clsTextosEstandar.TarifaUnica;
        var metodo = clsTextoSeguro.LimpiarLinea(payload.MetodoCalculo)?.ToLowerInvariant() ?? clsReglaMargen.MetodoMargen;
        var linea = clsTextoSeguro.LimpiarLinea(payload.LineaNegocio) ?? string.Empty;
        var ciudad = clsTextoSeguro.LimpiarLinea(payload.Ciudad) ?? clsFormatoFecha.CiudadPorDefecto;

        var secciones = new List<clsSeccionDocumento>();
        foreach (var seccion in clsValidadorCotizacionPdfPayload.SeccionesDe(payload))
        {
            var construida = ConstruirSeccion(seccion, metodo, esMulti, monedaPorDefecto);
            if (construida.EsFallo)
            {
                return construida.Error;
            }

            secciones.Add(construida.Valor);
        }

        // Incluye / no incluye: de la tarifa (o de la primera sección en multi), de "textos" o estándar. Los servicios
        // conexos itemizados (HU-003.5) mandan sobre cualquier lista de texto.
        var fuenteListas = payload.Tarifa ?? clsValidadorCotizacionPdfPayload.SeccionesDe(payload).FirstOrDefault();
        var incluye = Itemizado(payload, fuenteListas, monedaPorDefecto)
                      ?? Primera(fuenteListas?.Incluye, payload.Textos?.Incluye)
                      ?? textos.Incluye;
        // #173: "no incluye" nunca nombra un servicio que sí va itemizado (p. ej. "Acompañamiento vehicular" con el conexo
        // "Acompañante vehicular en moto"): la lista se depura contra los conexos reales de la cotización.
        var noIncluye = SinLoIncluido(Primera(fuenteListas?.NoIncluye, payload.Textos?.NoIncluye) ?? textos.NoIncluye, payload.ServiciosConexos);

        var clave = ClaveLinea(linea);
        var imagen = !esMulti && clsTextosEstandar.ImagenPorLinea.TryGetValue(clave, out var recurso) ? recurso : null;
        var leyenda = imagen is not null && clsTextosEstandar.LeyendaPorLinea.TryGetValue(clave, out var frase) ? frase : null;
        var modalidad = clsTextoSeguro.LimpiarLinea(payload.Modalidad);
        var fraseFinal = esMulti ? null : clsTextoSeguro.Limpiar(payload.FraseFinal);

        return new clsDocumentoCotizacion
        {
            Variante = esMulti ? VarianteDocumento.MultiSeccion : VarianteDocumento.TarifaUnica,
            FechaLarga = clsFormatoFecha.FechaLarga(clsTextoSeguro.LimpiarLinea(payload.Fecha), ciudad, hoy),
            Referencia = folio.Trim(),
            Cliente = clsTextoSeguro.LimpiarLinea(payload.Cliente) ?? string.Empty,
            Contactos = [.. clsTextoSeguro.LimpiarLista(payload.Contactos).Select(c => c.Replace('\n', ' '))],
            Asunto = clsTextoSeguro.LimpiarLinea(payload.Asunto) ?? $"presentación oferta de servicios de Transporte {linea}",
            Introduccion = Introduccion(Primera(payload.Textos?.Intro) ?? textos.Intro, esMulti),
            ImagenLinea = imagen,
            LeyendaImagen = leyenda,
            Subtitulo = esMulti
                ? null
                : $"TRANSPORTE {linea.ToUpperInvariant()}" + (modalidad is null ? ":" : $" EN CALIDAD DE {modalidad.ToUpperInvariant()}:"),
            Secciones = secciones,
            NotaGeneral = esMulti
                ? null
                : clsTextoSeguro.Limpiar(payload.Tarifa?.NotaGeneral) ?? clsTextoSeguro.Limpiar(payload.Textos?.NotaGeneral)
                  // #173: con las horas reales de la captura y contra los conexos incluidos (cargue / descargue).
                  ?? clsTextosEstandar.NotaGeneral(payload.HorasCargue, payload.HorasDescargue,
                      Cubre("Cargue", payload.ServiciosConexos), Cubre("Descargue", payload.ServiciosConexos)),
            NotaDecreto = esMulti ? null : clsTextoSeguro.Limpiar(payload.NotaDecreto) ?? clsTextosEstandar.NotaDecreto,
            NotaMoneda = enDolares ? clsTextosEstandar.NotaTrmMinima(trmAplicada) : null,
            BloquesAdicionales = BloquesAdicionales(payload, linea),
            Incluye = [.. incluye.Select(t => Celda(t))],
            NoIncluye = [.. noIncluye.Select(t => Celda(t))],
            Facturacion = clsTextoSeguro.Limpiar(payload.Textos?.Facturacion) ?? textos.Facturacion,
            Seguro = Parrafos(clsTextoSeguro.Limpiar(payload.Textos?.Seguro) ?? textos.Seguro),
            DiasLibres = esMulti ? null : clsTextoSeguro.Limpiar(payload.DiasLibres),
            TituloStandBy = esMulti ? clsTextosEstandar.TituloStandByMulti : clsTextosEstandar.TituloStandByUnica,
            StandBy = clsTextoSeguro.Limpiar(payload.Textos?.StandBy) ?? textos.StandBy,
            Vigencia = clsTextoSeguro.Limpiar(payload.Textos?.Vigencia) ?? textos.Vigencia,
            FraseFinal = fraseFinal is null
                ? new clsFraseFinalDocumento(clsTextosEstandar.FraseFinal, clsTextosEstandar.EnlaceWeb)
                : new clsFraseFinalDocumento(fraseFinal, null),
            Firma = firma,
        };
    }

    /// <summary>
    /// Tarifa de una fila (<c>calcular_tarifa</c>): <c>tarifa</c> si viene; si no, costo y margen con el método
    /// (<c>margen</c>: costo / (1 − m), <c>markup</c>: costo × (1 + m), a pesos half-to-even); sin datos → <c>null</c> ("—").
    /// Devuelve el error (<c>PDF_PAYLOAD_INVALIDO</c>) si el costo o el margen no permiten calcular, o <c>null</c> si todo va bien.
    /// </summary>
    public static clsErrorDominio? CalcularTarifa(clsFilaTarifaPayload fila, string? metodo, out decimal? tarifa)
    {
        ArgumentNullException.ThrowIfNull(fila);
        tarifa = null;
        if (fila.Tarifa is { } directa)
        {
            tarifa = directa;
            return null;
        }

        if (fila.Costo is not { } costo || fila.Margen is not { } margen)
        {
            return null;
        }

        var calculado = clsCalculadoraPrecio.Calcular(costo, margen, metodo);
        if (calculado.EsFallo)
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, $"No se puede calcular la tarifa de la fila '{fila.Destino}': {calculado.Error.Mensaje}");
        }

        tarifa = calculado.Valor.Precio;
        return null;
    }

    /// <summary>
    /// Clave de la línea para imagen y leyenda (<c>_norm</c> del Python): sin tildes, minúsculas y recortada.
    /// </summary>
    public static string ClaveLinea(string? linea)
    {
        if (string.IsNullOrWhiteSpace(linea))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(linea.Length);
        foreach (var c in linea.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Tramo máximo sin espacios en la columna "Destino" (15 % del ancho: ~8 caracteres anchos en negrita).</summary>
    public const int TramoDestino = 8;

    /// <summary>Tramo máximo sin espacios en la columna "Tipo de vehículo y peso" (22 % del ancho).</summary>
    public const int TramoVehiculo = 12;

    /// <summary>
    /// Texto de una celda o lista: permite partir palabras largas sin espacios (ver <see cref="clsTextoSeguro.PartirPalabrasLargas"/>).
    /// Por defecto cada 30 caracteres; las columnas angostas de la tabla usan tramos más cortos para que el tramo quepa.
    /// </summary>
    private static string Celda(string texto, int tramo = clsTextoSeguro.LargoMaximoPalabra) => clsTextoSeguro.PartirPalabrasLargas(texto, tramo);

    private static clsResultado<clsSeccionDocumento> ConstruirSeccion(clsTarifaPayload seccion, string metodo, bool esMulti, string? monedaPorDefecto)
    {
        var filas = new List<clsFilaDocumento>();
        foreach (var fila in seccion.Filas ?? [])
        {
            var error = CalcularTarifa(fila, metodo, out var tarifa);
            if (error is not null)
            {
                return error;
            }

            filas.Add(new clsFilaDocumento(
                Celda(clsTextoSeguro.LimpiarLinea(fila.Destino) ?? string.Empty, TramoDestino),
                Celda(clsTextoSeguro.LimpiarLinea(fila.VehiculoPeso) ?? string.Empty, TramoVehiculo),
                clsFormatoMoneda.Formatear(tarifa, clsTextoSeguro.LimpiarLinea(fila.Moneda) ?? monedaPorDefecto),
                Celda(clsTextoSeguro.Limpiar(fila.Nota) ?? string.Empty)));
        }

        var tituloPorDefecto = esMulti ? string.Empty : clsTextosEstandar.TituloTablaPorDefecto;
        return new clsSeccionDocumento(
            Celda(clsTextoSeguro.LimpiarLinea(seccion.Titulo) ?? tituloPorDefecto),
            Celda(clsTextoSeguro.LimpiarLinea(seccion.Origen) ?? string.Empty),
            filas,
            esMulti ? clsTextoSeguro.Limpiar(seccion.NotaGeneral) : null);
    }

    /// <summary>
    /// "Estas tarifas incluyen" itemizado: el flete (origen y destinos de las filas) y cada servicio conexo con su precio.
    /// <c>null</c> si el payload no trae servicios conexos.
    /// </summary>
    private static List<string>? Itemizado(clsCotizacionPdfPayload payload, clsTarifaPayload? tarifa, string? monedaPorDefecto)
    {
        var servicios = (payload.ServiciosConexos ?? [])
            .Where(s => clsTextoSeguro.LimpiarLinea(s?.Descripcion) is not null)
            .ToList();
        if (servicios.Count == 0)
        {
            return null;
        }

        var origen = clsTextoSeguro.LimpiarLinea(tarifa?.Origen);
        var destinos = clsValidadorCotizacionPdfPayload.SeccionesDe(payload)
            .SelectMany(s => s.Filas ?? [])
            .Select(f => clsTextoSeguro.LimpiarLinea(f?.Destino))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flete = origen is null && destinos.Count == 0
            ? clsTextosEstandar.TarifaUnica.Incluye[0]
            : $"Flete {origen ?? "origen"} – {(destinos.Count == 0 ? "destino" : string.Join(", ", destinos))}";

        var items = new List<string> { flete };
        items.AddRange(servicios.Select(s =>
        {
            var descripcion = clsTextoSeguro.LimpiarLinea(s.Descripcion)!;
            return s.Precio is { } precio ? $"{descripcion}: {clsFormatoMoneda.Formatear(precio, clsTextoSeguro.LimpiarLinea(s.Moneda) ?? monedaPorDefecto)}" : descripcion;
        }));
        return items;
    }

    private static List<clsBloqueDocumento> BloquesAdicionales(clsCotizacionPdfPayload payload, string linea)
    {
        var bloques = new List<clsBloqueDocumento>();
        if (linea.StartsWith("internacional", StringComparison.OrdinalIgnoreCase) && payload.Aduana is { } aduana)
        {
            AgregarBloque(bloques, "Datos aduaneros (ANDINO)",
                ("Modalidad aduanera", aduana.Modalidad), ("Frontera", aduana.Frontera), ("Arancel", aduana.Arancel));
        }

        if (payload.Extradimensionada is { } extra)
        {
            AgregarBloque(bloques, "Mercancía extradimensionada",
                ("Peso (kg)", extra.PesoKg), ("Ancho", extra.Ancho), ("Largo", extra.Largo), ("Fotos", extra.Fotos));
        }

        if (payload.Refrigerada is { } refrigerada)
        {
            AgregarBloque(bloques, "Mercancía refrigerada", ("Temperatura", refrigerada.Temperatura));
        }

        return bloques;
    }

    private static void AgregarBloque(List<clsBloqueDocumento> bloques, string titulo, params (string Etiqueta, string? Valor)[] datos)
    {
        var presentes = datos
            .Select(d => (d.Etiqueta, Valor: clsTextoSeguro.LimpiarLinea(d.Valor)))
            .Where(d => d.Valor is not null)
            .Select(d => new clsDatoDocumento(d.Etiqueta, Celda(d.Valor!)))
            .ToList();
        if (presentes.Count > 0)
        {
            bloques.Add(new clsBloqueDocumento(titulo, presentes));
        }
    }

    /// <summary>
    /// Introducción. En multi, los párrafos que empiezan por <c>·</c> o <c>•</c> son viñetas (<c>render_intro</c>); en tarifa
    /// única todos son párrafos, como en el Python.
    /// </summary>
    private static List<clsParrafoDocumento> Introduccion(IReadOnlyList<string> parrafos, bool esMulti) =>
        [.. parrafos.Select(clsTextoSeguro.Limpiar).OfType<string>().Select(p =>
            esMulti && (p.StartsWith('·') || p.StartsWith('•'))
                ? new clsParrafoDocumento(p.TrimStart('·', '•').Trim(), EsVineta: true)
                : new clsParrafoDocumento(p))];

    /// <summary>Párrafos separados por una línea en blanco (el seguro de <c>armar_multi.py</c>).</summary>
    private static List<string> Parrafos(string texto) =>
        [.. texto.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<string>? Primera(params List<string>?[] listas) =>
        listas.Select(clsTextoSeguro.LimpiarLista).FirstOrDefault(l => l.Count > 0);

    /// <summary>¿Algún conexo incluido cubre <paramref name="servicio"/>? (misma regla que <see cref="SinLoIncluido"/>).</summary>
    public static bool Cubre(string servicio, IEnumerable<clsServicioConexoPayload?>? conexos) =>
        SinLoIncluido([servicio], conexos).Count == 0;

    /// <summary>Largo de la raíz con que se comparan las palabras (acompañamiento / acompañante → "acompa").</summary>
    private const int LargoRaiz = 6;

    /// <summary>
    /// #173: quita de "no incluye" lo que cubre un servicio conexo incluido. Un texto queda cubierto si TODAS sus palabras significativas
    /// (4 letras o más, sin tildes ni mayúsculas) empiezan igual (<see cref="LargoRaiz"/> letras) que alguna palabra de la descripción de
    /// UN mismo conexo: "Acompañamiento vehicular" ↔ "Acompañante vehicular en moto", pero "Cargues y descargues" no se quita con un conexo
    /// "Cargue" solo.
    /// </summary>
    public static IReadOnlyList<string> SinLoIncluido(IReadOnlyList<string> noIncluye, IEnumerable<clsServicioConexoPayload?>? conexos)
    {
        ArgumentNullException.ThrowIfNull(noIncluye);
        var incluidos = (conexos ?? [])
            .Select(c => clsTextoSeguro.LimpiarLinea(c?.Descripcion))
            .OfType<string>()
            .Select(d => Raices(d).ToHashSet(StringComparer.Ordinal))
            .Where(r => r.Count > 0)
            .ToList();
        if (incluidos.Count == 0)
        {
            return noIncluye;
        }

        return [.. noIncluye.Where(texto =>
        {
            var raices = Raices(texto).ToList();
            return raices.Count == 0 || !incluidos.Any(conexo => raices.All(conexo.Contains));
        })];
    }

    private static IEnumerable<string> Raices(string texto) =>
        SinTildes(texto).ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string([.. p.Where(char.IsLetter)]))
            .Where(p => p.Length >= 4)
            .Select(p => p.Length > LargoRaiz ? p[..LargoRaiz] : p);

    private static string SinTildes(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                limpio.Append(c);
            }
        }

        return limpio.ToString().Normalize(NormalizationForm.FormC);
    }
}
