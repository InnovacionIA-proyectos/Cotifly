using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>
/// Detección de la columna de mercancía de un Excel y código sugerido por fila contra el catálogo del cubicador (HU-023.5, #160). Nació
/// en el análisis del pliego de licitaciones y HU-035.1 (#202) la sacó aquí, sin nada de Nexo ni de licitaciones, para reutilizarla en
/// el Excel de mercancías de una ruta. Determinista: solo la lectura del catálogo (<see cref="CatalogoAsync"/>) llama al cubicador.
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
            // HU-035.1: el adaptador traduce sus fallos a un resultado, pero una excepción inesperada tampoco tumba el análisis.
            logger.LogWarning(ex, "El catálogo de tipos de mercancía del cubicador falló: el análisis va sin sugerencias de código.");
            return null;
        }
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

        // Solo los tipos cubicables: uno que no se puede cubicar se rechazaría al guardar (HU-023.3).
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
