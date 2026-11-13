using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary><c>POST /web/licitaciones/aplicar-mapeo</c>.</summary>
public interface IClsAplicacionMapeoService
{
    /// <summary>
    /// Aplica en NEXO el mapeo confirmado a todas las filas y traduce cada línea a la línea de licitación de Cotifly. Errores: los de la
    /// validación local (<see cref="clsValidadorAplicacionMapeo"/>), los 400 del mapeo de NEXO con su código (<c>CAMPO_DUPLICADO</c>,
    /// <c>CAMPO_REQUERIDO</c>, <c>TRANSFORMACION_INVALIDA</c>...), <c>IA_TOPE_DIARIO</c>, <c>NEXO_SIN_PERMISO</c> y <c>NEXO_NO_DISPONIBLE</c>.
    /// Los problemas de una fila no son un error de la petición: van en <c>errores[]</c>. Una fila con errores de NEXO va solo en
    /// <c>errores[]</c>; una fila cuya línea de negocio no tiene equivalencia en Cotifly va en <c>lineas[]</c> (sin línea) <b>y</b> en
    /// <c>errores[]</c> (<c>LINEA_NEGOCIO_SIN_EQUIVALENCIA</c>).
    /// </summary>
    Task<clsResultado<clsMapeoAplicado>> AplicarAsync(Guid usuarioId, clsEntradaAplicacionMapeo entrada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsAplicacionMapeoService"/>. NEXO convierte las filas con las reglas de su captura (diccionario del
/// cliente por NIT, transformaciones como toneladas a kg o «ORIGEN - DESTINO», coma decimal) por el puerto de la importación
/// (<see cref="IClsImportacionExcelNexo"/>, sin IA: se reintenta una vez). Cotifly traduce cada <see cref="clsLineaImportada"/> a
/// <see cref="clsLineaMapeada"/> (<c>LineaLicitacionImportada</c>, subconjunto de <c>LineaLicitacionWeb</c> con los mismos nombres, más
/// <c>fila_excel</c> y <c>linea_nexo</c>), campo por campo:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b><c>fila</c> y <c>fila_excel</c></b>: <c>fila</c> es la posición de la fila en las <c>filas</c> enviadas (base 0) = <c>fila</c> de
/// NEXO − <c>primera_fila</c> (lo que espera <c>LineaLicitacionWeb.fila</c>); <c>fila_excel</c> es el número de Excel que dio NEXO (también en
/// los errores). <c>fila</c> queda <c>null</c> si NEXO numeró fuera de las enviadas (defensa).</item>
/// <item><b><c>linea_negocio</c> y <c>linea_nexo</c></b>: <c>linea_negocio</c> es la de Cotifly, con la misma traducción de
/// <c>normalizar-valores</c> (<see cref="clsTraductorLineaNegocio"/>, ADR-029), y <c>linea_nexo</c> la de NEXO. Si NEXO trae una línea sin
/// equivalencia en Cotifly (ninguna la mapea, inactiva, mapeo inválido o varias), la fila vuelve igual en <c>lineas[]</c> (con
/// <c>linea_negocio = null</c>) <b>y</b> en <c>errores[]</c> (<c>LINEA_NEGOCIO_SIN_EQUIVALENCIA</c>): el comercial elige. Vacía: sin línea
/// propia (vale la de la licitación), sin error.</item>
/// <item><b><c>peso</c> → <c>peso_kg</c></b> (la carga, sin tara). <c>peso_con_tara_kg</c> nunca sale del
/// Excel: la tara solo aplica a Contenedor y la pone el comercial.</item>
/// <item><b><c>posiciones</c> → <c>mercancias[]</c></b>: <c>descripcion</c> → <c>descripcion</c> y <c>nombre_mercancia</c> (los dos nombres del
/// campo), cantidad, medidas en cm (las tres <c>null</c> si NEXO no trae ninguna) y <c>peso_kg</c> por unidad. <c>codigoTipoMercancia</c> →
/// <c>codigo_mercancia</c> tal cual; sin él, el tipo cubicable más parecido a la descripción en el catálogo del cubicador
/// (<see cref="clsSugeridorMercancia"/>, como el análisis del Excel); sin sugerencia (o sin catálogo), sin código: lo escoge el comercial.</item>
/// <item>Los ids de municipio no salen (los resuelve <c>POST /web/licitaciones</c>): van siempre en <c>null</c>. <c>referencia</c> y
/// <c>deadline</c> son de la solicitud, no de la línea, y se descartan. Los demás campos pasan con su mismo nombre.</item>
/// </list>
/// <para>Nada de esto reemplaza las validaciones de <c>POST /web/licitaciones</c>: la línea vuelve para revisarse y se valida al crear.</para>
/// </remarks>
public sealed class clsAplicacionMapeoService(
    IClsImportacionExcelNexo nexo,
    IClsMargenPoliticaRepository politicas,
    IClsColumnasDerivadasService columnasDerivadas,
    ILogger<clsAplicacionMapeoService> logger,
    IClsCubicador? cubicador = null) : IClsAplicacionMapeoService
{
    /// <summary>Campo de Cotifly de los errores de la línea de negocio.</summary>
    public const string CampoLineaNegocio = "linea_negocio";

    /// <summary>Campo de un error de NEXO que no es de un campo (toda la fila).</summary>
    public const string CampoFila = "fila";

    public async Task<clsResultado<clsMapeoAplicado>> AplicarAsync(
        Guid usuarioId, clsEntradaAplicacionMapeo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "Aplicar el mapeo exige el comercial autenticado.");
        }

        // Antes de validar y mandar a NEXO, Cotifly agrega al final de cada fila las mismas columnas derivadas que propuso
        // sugerir-mapeo (vehículo en el texto, contenedor con peso), con los índices que el mapeo confirmado ya referencia. NEXO las convierte
        // como cualquier otra columna. Una petición mal formada (filas nulas) no se enriquece: la validación la rechaza igual. Si el mapeo
        // usa una columna después de los encabezados, las derivadas se agregan aunque esta vez no se reconozca nada (NEXO caído).
        var forzar = entrada.NumeroEncabezados is { } encabezados && entrada.Columnas?.Any(c => c?.Indice >= encabezados) == true;
        var filasEnriquecidas = await columnasDerivadas.EnriquecerFilasAsync(
            usuarioId, entrada.Filas ?? [], entrada.NumeroEncabezados, forzar, cancellationToken);
        var peticion = clsValidadorAplicacionMapeo.Validar(entrada with { Filas = filasEnriquecidas });
        if (peticion.EsFallo)
        {
            return peticion.Error;
        }

        var respuesta = await nexo.AplicarMapeoAsync(usuarioId, peticion.Valor, cancellationToken);
        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        var deNexo = respuesta.Valor;
        var primera = peticion.Valor.PrimeraFila ?? clsValidadorAplicacionMapeo.PrimeraFilaPorDefecto;
        var totalFilas = peticion.Valor.Filas.Count;
        // La posición en las filas enviadas; null si NEXO numeró fuera de ellas (no debería: solo por defensa).
        int? Indice(int filaExcel)
        {
            var posicion = filaExcel - primera;
            return posicion >= 0 && posicion < totalFilas ? posicion : null;
        }

        IReadOnlyList<clsMargenPolitica> lineasNegocio = deNexo.Lineas.Any(l => Texto(l.LineaNegocio) is not null)
            ? await politicas.ListarAsync(cancellationToken)
            : [];
        var sugeridor = await SugeridorAsync(deNexo.Lineas, cancellationToken);

        var lineas = new List<clsLineaMapeada>(deNexo.Lineas.Count);
        var erroresCotifly = new List<clsErrorFilaImportada>();
        foreach (var importada in deNexo.Lineas)
        {
            string? lineaNegocio = null;
            string? lineaNexo = null;
            if (Texto(importada.LineaNegocio) is { } texto)
            {
                var traducida = clsTraductorLineaNegocio.Traducir(texto, lineasNegocio);
                lineaNexo = traducida.LineaNexo;
                if (traducida.EsResuelta)
                {
                    lineaNegocio = traducida.LineaCotifly;
                }
                else
                {
                    var opciones = traducida.Candidatas is { Count: > 0 } c ? $" Opciones: {string.Join(", ", c)}." : string.Empty;
                    erroresCotifly.Add(new clsErrorFilaImportada(Indice(importada.Fila), importada.Fila, CampoLineaNegocio,
                        clsCodigosError.LineaNegocioSinEquivalencia,
                        $"Fila {importada.Fila} del Excel, línea de negocio '{texto}': {traducida.Motivo}{opciones}"));
                }
            }

            var mercancias = importada.Posiciones?.Select(p => Mercancia(p, sugeridor)).ToList();
            lineas.Add(new clsLineaMapeada(
                Indice(importada.Fila), importada.Fila, lineaNegocio, lineaNexo, importada.Origen.Trim(), importada.Destino.Trim(),
                null, null, importada.HorasCargue, importada.HorasDescargue, Texto(importada.TipoVehiculo), Texto(importada.Carroceria),
                Texto(importada.Tipologia), Texto(importada.ClaseViaje), Texto(importada.ClaseContenedor), importada.Exclusivo,
                importada.Consolidado, importada.RetornaContenedor, Texto(importada.CiudadDevolucionContenedor), null,
                importada.LevanteContenedorDiferente, Texto(importada.CiudadLevanteContenedor), null, importada.Peso,
                importada.ValorMercancia, mercancias));
        }

        var propios = erroresCotifly.Count;
        var errores = new List<clsErrorFilaImportada>(propios + deNexo.Errores.Count);
        errores.AddRange(erroresCotifly);
        // Los errores de NEXO llevan su campo tal como lo manda NEXO; sin campo, toda la fila.
        errores.AddRange(deNexo.Errores.Select(e => new clsErrorFilaImportada(
            Indice(e.Fila), e.Fila, string.IsNullOrWhiteSpace(e.Campo) ? CampoFila : e.Campo.Trim(), e.Codigo, e.Mensaje)));

        LogAplicado(logger, totalFilas, lineas.Count, deNexo.TotalErrores, propios);
        return new clsMapeoAplicado(
            deNexo.TotalFilas, deNexo.FilasVacias, deNexo.FilasConErrores + propios, deNexo.TotalErrores + propios,
            lineas, [.. errores.OrderBy(e => e.FilaExcel)]);
    }

    /// <summary>
    /// Una posición de NEXO como mercancía de Cotifly. Sin código, el sugerido por la descripción o ninguno (lo escoge el comercial). La
    /// descripción de NEXO va en <c>descripcion</c> y en <c>nombre_mercancia</c> (los dos nombres del campo en el alta).
    /// </summary>
    private static clsMercanciaMapeada Mercancia(clsPosicionImportada posicion, Func<string, clsMercanciaSugerida?> sugeridor)
    {
        var descripcion = Texto(posicion.Descripcion);
        var codigo = Texto(posicion.CodigoTipoMercancia) ?? (descripcion is null ? null : sugeridor(descripcion)?.Tipo.Codigo);
        var sinMedidas = posicion.LargoCm == 0 && posicion.AnchoCm == 0 && posicion.AltoCm == 0;
        return new clsMercanciaMapeada(codigo, descripcion, descripcion, posicion.Cantidad,
            sinMedidas ? null : posicion.LargoCm, sinMedidas ? null : posicion.AnchoCm, sinMedidas ? null : posicion.AltoCm, posicion.PesoKg);
    }

    /// <summary>
    /// El sugeridor de códigos sobre los tipos cubicables del catálogo del cubicador (con caché por descripción). El catálogo se lee una vez
    /// y solo si alguna posición no trae código; sin cubicador o si no responde, nunca sugiere (la importación no falla por esto).
    /// </summary>
    private async Task<Func<string, clsMercanciaSugerida?>> SugeridorAsync(IReadOnlyList<clsLineaImportada> lineas, CancellationToken cancellationToken)
    {
        var necesita = lineas.Any(l => l.Posiciones?.Any(p => Texto(p.CodigoTipoMercancia) is null && Texto(p.Descripcion) is not null) == true);
        var catalogo = necesita ? await clsDetectorMercanciaExcel.CatalogoAsync(cubicador, logger, cancellationToken) : null;
        if (catalogo is null)
        {
            return _ => null;
        }

        var cubicables = catalogo.Where(t => t.Cubicable).ToList();
        var cache = new Dictionary<string, clsMercanciaSugerida?>(StringComparer.Ordinal);
        return texto =>
        {
            if (!cache.TryGetValue(texto, out var sugerida))
            {
                sugerida = clsSugeridorMercancia.Sugerir(texto, cubicables);
                cache[texto] = sugerida;
            }

            return sugerida;
        };
    }

    private static string? Texto(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static void LogAplicado(ILogger logger, int filas, int lineas, int erroresNexo, int erroresCotifly) =>
        logger.LogInformation(
            "Aplicar mapeo: {Filas} filas, {Lineas} líneas, {ErroresNexo} errores de NEXO y {ErroresCotifly} de línea de negocio de Cotifly.",
            filas, lineas, erroresNexo, erroresCotifly);
}
