using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>
/// Detección de la columna de mercancía de un Excel y código sugerido por fila contra el catálogo del cubicador. No depende
/// de Nexo ni de licitaciones: la usan el análisis del pliego de licitaciones y el Excel de mercancías de una ruta. Determinista: solo la lectura del catálogo (<see cref="CatalogoAsync"/>) llama al cubicador.
/// </summary>
public static class clsDetectorMercanciaExcel
{
    /// <summary>
    /// Catálogo de tipos de mercancía del cubicador (con su caché), o <c>null</c> sin cubicador configurado o si no responde: quien
    /// lo usa sigue sin sugerencias, nunca falla por esto. Respeta la cancelación del llamador.
    /// </summary>
    public static async Task<IReadOnlyList<clsTipoMercanciaCubicador>?> CatalogoAsync(
        IClsCubicador? cubicador, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (cubicador is not { EstaConfigurado: true })
        {
            return null;
        }

        try
        {
            var leido = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
            if (leido.EsExitoso)
            {
                return leido.Valor;
            }

            logger.LogInformation("El catálogo de tipos de mercancía del cubicador no respondió ({Codigo}): el análisis va sin sugerencias de código.",
                leido.Error.Codigo);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // El adaptador traduce sus fallos a un resultado, pero una excepción inesperada tampoco tumba el análisis.
            logger.LogWarning(ex, "El catálogo de tipos de mercancía del cubicador falló: el análisis va sin sugerencias de código.");
            return null;
        }
    }

    /// <summary>
    /// Resultado de <see cref="ConMercanciasAsync"/>: las hojas (o tablas) con la mercancía detectada, si el catálogo del cubicador
    /// respondió (<see cref="CatalogoDisponible"/>) y si de verdad se tocó alguna hoja (<see cref="Aplicado"/> = alguna tenía columna de
    /// mercancía o de código; <c>false</c> = las hojas quedan como estaban).
    /// </summary>
    public sealed record clsHojasConMercancias(IReadOnlyList<clsHojaExcel> Hojas, bool CatalogoDisponible, bool Aplicado);

    /// <summary>
    /// A cada hoja (o tabla) con columna de mercancía o de código le agrega
    /// <see cref="clsHojaExcel.ColumnaMercancia"/>, <see cref="clsHojaExcel.ColumnaCodigoMercancia"/> y <see cref="clsHojaExcel.Mercancias"/>
    /// (por fila: el código de la columna del Excel tal cual o el más parecido del catálogo del cubicador). El catálogo se lee una sola vez
    /// (con caché) y solo si alguna hoja tiene una de esas columnas; si no responde, el detalle sigue sin sugerencias por parecido. Es la
    /// misma detección de <c>analizar-excel</c>, reutilizada por <c>leer-estructura</c> (sus tablas). No toca <see cref="clsHojaExcel.FilasExcel"/>.
    /// </summary>
    public static async Task<clsHojasConMercancias> ConMercanciasAsync(
        IReadOnlyList<clsHojaExcel> hojas, IClsCubicador? cubicador, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hojas);
        ArgumentNullException.ThrowIfNull(logger);
        var columnas = hojas.Select(h => (Hoja: h, Texto: ColumnaMercancia(h.Encabezados), Codigo: ColumnaCodigoMercancia(h.Encabezados))).ToList();
        if (columnas.All(c => c.Texto is null && c.Codigo is null))
        {
            return new clsHojasConMercancias(hojas, false, false);
        }

        var catalogo = await CatalogoAsync(cubicador, logger, cancellationToken);
        var porCodigo = PorCodigo(catalogo);
        var nuevas = columnas.Select(c => c.Texto is null && c.Codigo is null
            ? c.Hoja
            : c.Hoja with
            {
                ColumnaMercancia = c.Texto is { } t ? c.Hoja.Encabezados[t] : null,
                ColumnaCodigoMercancia = c.Codigo is { } k ? c.Hoja.Encabezados[k] : null,
                Mercancias = Mercancias(c.Hoja.Filas, c.Texto, c.Codigo, catalogo, porCodigo),
            }).ToList();
        return new clsHojasConMercancias(nuevas, catalogo is not null, true);
    }

    /// <summary>El catálogo por código (sin distinguir mayúsculas); vacío sin catálogo.</summary>
    public static IReadOnlyDictionary<string, clsTipoMercanciaCubicador> PorCodigo(IReadOnlyList<clsTipoMercanciaCubicador>? catalogo)
    {
        var porCodigo = new Dictionary<string, clsTipoMercanciaCubicador>(StringComparer.OrdinalIgnoreCase);
        foreach (var tipo in catalogo ?? [])
        {
            porCodigo.TryAdd(tipo.Codigo, tipo);
        }

        return porCodigo;
    }

    /// <summary>Por cada fila con mercancía (texto o código), su código (ver <see cref="clsCodigoMercanciaFila"/>).</summary>
    public static IReadOnlyList<clsCodigoMercanciaFila> Mercancias(
        IReadOnlyList<IReadOnlyList<string>> filas,
        int? columnaTexto,
        int? columnaCodigo,
        IReadOnlyList<clsTipoMercanciaCubicador>? catalogo,
        IReadOnlyDictionary<string, clsTipoMercanciaCubicador>? porCodigo = null)
    {
        ArgumentNullException.ThrowIfNull(filas);
        static string? Celda(IReadOnlyList<string> fila, int? columna) =>
            columna is { } c && c < fila.Count && !string.IsNullOrWhiteSpace(fila[c]) ? fila[c].Trim() : null;

        // Solo los tipos cubicables: uno que no se puede cubicar se rechazaría al guardar.
        var cubicables = catalogo?.Where(t => t.Cubicable).ToList();
        var sugerencias = new Dictionary<string, clsMercanciaSugerida?>(StringComparer.Ordinal);
        var resultado = new List<clsCodigoMercanciaFila>();
        for (var i = 0; i < filas.Count; i++)
        {
            var texto = Celda(filas[i], columnaTexto);
            var codigo = Celda(filas[i], columnaCodigo);
            if (codigo is not null)
            {
                // La columna de código del Excel se usa tal cual (la validación de la licitación revisa que exista en el catálogo).
                var nombre = porCodigo is not null && porCodigo.TryGetValue(codigo, out var tipo) ? tipo.Nombre : null;
                resultado.Add(new clsCodigoMercanciaFila(i, texto, codigo, nombre, Sugerido: false, clsCodigoMercanciaFila.OrigenExcel, null));
                continue;
            }

            if (texto is null)
            {
                continue;
            }

            if (!sugerencias.TryGetValue(texto, out var sugerencia))
            {
                sugerencia = cubicables is null ? null : clsSugeridorMercancia.Sugerir(texto, cubicables);
                sugerencias[texto] = sugerencia;
            }

            resultado.Add(sugerencia is null
                ? new clsCodigoMercanciaFila(i, texto, null, null, Sugerido: false, null, null)
                : new clsCodigoMercanciaFila(i, texto, sugerencia.Tipo.Codigo, sugerencia.Tipo.Nombre, Sugerido: true, clsCodigoMercanciaFila.OrigenParecido,
                    Math.Round((decimal)sugerencia.Parecido, 4, MidpointRounding.AwayFromZero)));
        }

        return resultado;
    }

    /// <summary>
    /// Índice de la columna con la descripción de la mercancía, por su encabezado (sin tildes ni mayúsculas): primero uno que diga
    /// "mercancía", luego "PRODUCTO", "tipo de carga" o "carga", "commodity" y por último "descripción". <c>null</c> si no hay. La columna
    /// de código (<see cref="ColumnaCodigoMercancia"/>) no cuenta.
    /// </summary>
    public static int? ColumnaMercancia(IReadOnlyList<string> encabezados)
    {
        ArgumentNullException.ThrowIfNull(encabezados);
        var normalizados = encabezados.Select(e => clsSugeridorMercancia.Normalizar(e)).ToList();
        Func<string, bool>[] reglas =
        [
            e => Palabras(e).Any(p => p.StartsWith("MERCANC", StringComparison.Ordinal)),
            e => Palabras(e).Any(p => p.StartsWith("PRODUCTO", StringComparison.Ordinal)),
            e => e is "CARGA" or "TIPO DE CARGA" or "DESCRIPCION DE LA CARGA" or "DESCRIPCION CARGA",
            e => Palabras(e).Contains("COMMODITY"),
            e => Palabras(e).Any(p => p.StartsWith("DESCRIPCION", StringComparison.Ordinal)),
        ];
        foreach (var regla in reglas)
        {
            for (var i = 0; i < normalizados.Count; i++)
            {
                if (!EsCodigo(normalizados[i]) && regla(normalizados[i]))
                {
                    return i;
                }
            }
        }

        return null;
    }

    /// <summary>Índice de la columna con el código de la mercancía ("Código mercancía", "codigo_mercancia", "Cód. producto"...), o <c>null</c>.</summary>
    public static int? ColumnaCodigoMercancia(IReadOnlyList<string> encabezados)
    {
        ArgumentNullException.ThrowIfNull(encabezados);
        for (var i = 0; i < encabezados.Count; i++)
        {
            var normalizado = clsSugeridorMercancia.Normalizar(encabezados[i]);
            if (EsCodigo(normalizado)
                && Palabras(normalizado).Any(p => p.StartsWith("MERCANC", StringComparison.Ordinal) || p.StartsWith("PRODUCTO", StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return null;
    }

    private static string[] Palabras(string normalizado) => normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool EsCodigo(string normalizado) => Palabras(normalizado).Any(p => p is "CODIGO" or "COD" or "CODE");
}
