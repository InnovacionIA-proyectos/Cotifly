using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>HU-044.4 (#255): <c>POST /web/licitaciones/normalizar-valores</c>.</summary>
public interface IClsNormalizacionValoresService
{
    /// <summary>
    /// Lleva los textos distintos del Excel (vehículo, carrocería, ciudad y línea de negocio) a los valores oficiales con NEXO
    /// (diccionario del cliente, alias, catálogo y, al final, IA con lista cerrada). Errores: los de la validación local
    /// (<see cref="clsValidadorNormalizacionValores"/>), <c>CLIENTE_NIT_INVALIDO</c>, <c>IA_TOPE_DIARIO</c>, <c>NEXO_SIN_PERMISO</c> y
    /// <c>NEXO_NO_DISPONIBLE</c>. La IA de NEXO apagada o caída no es un error: <c>ia_usada = false</c> con lo que resolvieron las reglas.
    /// </summary>
    Task<clsResultado<clsNormalizacionValores>> NormalizarAsync(Guid usuarioId, clsEntradaNormalizacion entrada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsNormalizacionValoresService"/> (ADR-029). Reenvía a NEXO por el puerto de la importación
/// (<see cref="IClsImportacionExcelNexo"/>, #252) y traduce la línea de negocio a la de Cotifly.
/// </summary>
/// <remarks>
/// <para><b>Línea de negocio.</b> En una licitación cada ruta lleva su línea de negocio DE COTIFLY (decisión del 2026-11-05), que es la
/// que tiene margen; a NEXO viaja su <c>mapeoLineasNexo</c>. Por eso:</para>
/// <list type="number">
/// <item>Un texto que ya es el nombre de una línea de Cotifly (misma regla que el alta, <see cref="clsReglasMargen.MismaLinea"/>) se
/// resuelve aquí, sin NEXO (<c>origen = linea_cotifly</c>). Así «Distribución», que no tiene línea en NEXO, no queda en manos de la IA de
/// NEXO, que la llevaría a otra línea con otro margen. Si esa línea está inactiva o su mapeo no es una de las seis de NEXO, queda
/// <c>sin_resolver</c> con el motivo, como lo rechazaría el alta.</item>
/// <item>Cualquier otro texto va a NEXO. Lo que NEXO devuelve se valida con la lista cerrada de la fase 0 (<see cref="clsLineasNegocioNexo"/>):
/// fuera de las seis queda <c>sin_resolver</c>. Dentro, se busca la línea activa de Cotifly cuyo <c>mapeoLineasNexo</c> es esa línea: una
/// sola, se devuelve en <c>codigo</c>; ninguna (p. ej. <c>Licitaciones</c>), <c>sin_resolver</c>; varias, <c>ambiguo</c> con
/// <c>lineas_candidatas</c>.</item>
/// </list>
/// <para><b>Uno por valor.</b> NEXO agrupa los textos que para él son iguales; Cotifly devuelve un resultado por cada valor distinto que
/// mandó el front, en su orden, emparejado con el de NEXO por campo y texto. Si NEXO no devolvió alguno, sale <c>sin_resolver</c>.</para>
/// </remarks>
public sealed class clsNormalizacionValoresService(IClsImportacionExcelNexo nexo, IClsMargenPoliticaRepository politicas)
    : IClsNormalizacionValoresService
{
    public const string NotaSinRespuestaNexo = "NEXO no devolvió un resultado para este valor.";

    public async Task<clsResultado<clsNormalizacionValores>> NormalizarAsync(
        Guid usuarioId, clsEntradaNormalizacion entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "La normalización exige el comercial autenticado.");
        }

        var validados = clsValidadorNormalizacionValores.Validar(entrada);
        if (validados.EsFallo)
        {
            return validados.Error;
        }

        var valores = validados.Valor;
        IReadOnlyList<clsMargenPolitica> lineas = valores.Any(v => v.Campo == clsCamposNormalizacion.LineaNegocio)
            ? await politicas.ListarAsync(cancellationToken)
            : [];

        var resultados = new clsValorNormalizacionWeb?[valores.Count];
        for (var i = 0; i < valores.Count; i++)
        {
            if (valores[i].Campo == clsCamposNormalizacion.LineaNegocio)
            {
                resultados[i] = LineaDeCotifly(valores[i].Valor!, lineas);
            }
        }

        var pendientes = Enumerable.Range(0, valores.Count).Where(i => resultados[i] is null).ToList();
        if (pendientes.Count == 0)
        {
            return new clsNormalizacionValores(false, clsCamposNormalizacion.MotivoSinPendientes, [.. resultados.Select(r => r!)]);
        }

        var consulta = pendientes.Select(i => new clsValorANormalizar(clsCamposNormalizacion.ANexo(valores[i].Campo)!, valores[i].Valor)).ToList();
        var respuesta = await nexo.NormalizarValoresAsync(usuarioId, entrada.NitCliente, consulta, cancellationToken);
        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        var deNexo = respuesta.Valor.Valores;
        foreach (var i in pendientes)
        {
            var campoNexo = clsCamposNormalizacion.ANexo(valores[i].Campo)!;
            var deEse = deNexo.Where(v => string.Equals(v.Campo, campoNexo, StringComparison.OrdinalIgnoreCase)).ToList();
            var clave = Clave(valores[i].Valor);
            var propio = deEse.FirstOrDefault(v => string.Equals(v.Valor?.Trim(), valores[i].Valor, StringComparison.Ordinal))
                ?? (clave.Length == 0 ? null : deEse.FirstOrDefault(v => Clave(v.Valor) == clave));
            resultados[i] = propio is null
                ? SinResolver(valores[i].Campo!, valores[i].Valor!, clsCamposNormalizacion.OrigenNinguno, NotaSinRespuestaNexo)
                : valores[i].Campo == clsCamposNormalizacion.LineaNegocio
                    ? LineaDeNexo(valores[i].Valor!, propio, lineas)
                    : DeNexo(valores[i].Campo!, valores[i].Valor!, propio);
        }

        return new clsNormalizacionValores(respuesta.Valor.IaUsada, respuesta.Valor.MotivoSinIa, [.. resultados.Select(r => r!)]);
    }

    /// <summary>
    /// La línea de Cotifly con ese nombre, resuelta sin NEXO; <c>null</c> si el texto no es el nombre de ninguna (entonces va a NEXO).
    /// Misma elección que el alta (<c>clsMargenResolverService</c>): la que coincide exacto sin mayúsculas y luego la primera por nombre.
    /// </summary>
    private static clsValorNormalizacionWeb? LineaDeCotifly(string valor, IReadOnlyList<clsMargenPolitica> lineas)
    {
        var linea = lineas
            .Where(p => clsReglasMargen.MismaLinea(p.LineaNegocio, valor))
            .OrderBy(p => string.Equals(p.LineaNegocio, valor, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.LineaNegocio, StringComparer.Ordinal)
            .FirstOrDefault();
        if (linea is null)
        {
            return null;
        }

        var campo = clsCamposNormalizacion.LineaNegocio;
        if (!linea.Activa)
        {
            return SinResolver(campo, valor, clsCamposNormalizacion.OrigenLineaCotifly, clsMargenResolverService.LineaInactiva(linea.LineaNegocio).Mensaje);
        }

        if (clsMargenResolverService.LineaNexoInvalida(linea) is { } invalida)
        {
            return SinResolver(campo, valor, clsCamposNormalizacion.OrigenLineaCotifly, invalida.Mensaje);
        }

        var nota = linea.MapeoLineasNexo is null ? "Línea de Cotifly sin línea en NEXO: la ruta viaja sin línea de negocio." : null;
        return new clsValorNormalizacionWeb(campo, valor, clsCamposNormalizacion.Resuelto, linea.LineaNegocio, linea.LineaNegocio, null,
            clsCamposNormalizacion.OrigenLineaCotifly, 1.0, nota, null, clsLineasNegocioNexo.Canonica(linea.MapeoLineasNexo), null);
    }

    /// <summary>
    /// La línea de NEXO de <paramref name="nexo"/> (validada con las seis de la fase 0) traducida a la línea activa de Cotifly que la mapea.
    /// </summary>
    private static clsValorNormalizacionWeb LineaDeNexo(string valor, clsValorNormalizado nexo, IReadOnlyList<clsMargenPolitica> lineas)
    {
        var campo = clsCamposNormalizacion.LineaNegocio;
        if (Estado(nexo.Estado) == clsCamposNormalizacion.SinResolver || (string.IsNullOrWhiteSpace(nexo.Codigo) && string.IsNullOrWhiteSpace(nexo.Descripcion)))
        {
            return SinResolver(campo, valor, nexo.Origen, nexo.Nota);
        }

        var lineaNexo = clsLineasNegocioNexo.Canonica(nexo.Codigo) ?? clsLineasNegocioNexo.Canonica(nexo.Descripcion);
        if (lineaNexo is null)
        {
            return SinResolver(campo, valor, nexo.Origen,
                $"NEXO propuso '{nexo.Codigo ?? nexo.Descripcion}', que no es una de sus líneas de negocio ({clsLineasNegocioNexo.Lista}).");
        }

        var deCotifly = lineas
            .Where(p => p.Activa && clsLineasNegocioNexo.Canonica(p.MapeoLineasNexo) == lineaNexo)
            .Select(p => p.LineaNegocio)
            .Order(StringComparer.Ordinal)
            .ToList();
        // Una sola línea de Cotifly: resuelto aunque NEXO dijera "ambiguo" (sus opciones serían líneas de NEXO, no de Cotifly): nunca un
        // ambiguo sin opciones. Con origen "ia" sigue siendo una propuesta que el comercial confirma.
        return deCotifly.Count switch
        {
            0 => SinResolver(campo, valor, nexo.Origen, SinLineaDeCotifly(lineaNexo, lineas)) with { LineaNexo = lineaNexo },
            1 => new clsValorNormalizacionWeb(campo, valor, clsCamposNormalizacion.Resuelto, deCotifly[0], deCotifly[0], null, nexo.Origen,
                Confianza(nexo.Confianza), nexo.Nota, null, lineaNexo, null),
            _ => new clsValorNormalizacionWeb(campo, valor, clsCamposNormalizacion.Ambiguo, null, null, null, nexo.Origen, Confianza(nexo.Confianza),
                $"NEXO la reconoce como '{lineaNexo}', que corresponde a varias líneas de Cotifly: elige una.", null, lineaNexo, deCotifly),
        };
    }

    /// <summary>
    /// Por qué una línea de NEXO no tiene línea de Cotifly. <c>Licitaciones</c> usa el mensaje del alta
    /// (<see cref="clsMargenResolverService.LineaDesconocida"/> con <c>esLicitacion</c>): es la línea de NEXO y no da margen.
    /// </summary>
    private static string SinLineaDeCotifly(string lineaNexo, IReadOnlyList<clsMargenPolitica> lineas) =>
        lineaNexo == clsMargenResolverService.LineaNexoLicitaciones
            ? clsMargenResolverService.LineaDesconocida(lineaNexo,
                [.. lineas.Where(p => p.Activa).Select(p => p.LineaNegocio).Order(StringComparer.Ordinal)], esLicitacion: true).Mensaje
            : $"NEXO la reconoce como '{lineaNexo}', pero ninguna línea de negocio activa de Cotifly corresponde a esa línea: elígela a mano.";

    /// <summary>
    /// Vehículo, carrocería o ciudad: lo de NEXO con el campo en el nombre de Cotifly. Un estado que Cotifly no conoce es
    /// <c>sin_resolver</c> (sin código: no se propone) y la confianza se acota a [0, 1].
    /// </summary>
    private static clsValorNormalizacionWeb DeNexo(string campo, string valor, clsValorNormalizado nexo)
    {
        var origen = string.IsNullOrWhiteSpace(nexo.Origen) ? clsCamposNormalizacion.OrigenNinguno : nexo.Origen;
        var estado = Estado(nexo.Estado);
        return estado == clsCamposNormalizacion.SinResolver
            ? SinResolver(campo, valor, origen, nexo.Nota)
            : new(campo, valor, estado, nexo.Codigo, nexo.Descripcion, nexo.MunicipioId, origen, Confianza(nexo.Confianza), nexo.Nota,
                nexo.Candidatos is { Count: > 0 } candidatos ? candidatos : null, null, null);
    }

    /// <summary>El estado de NEXO si es <c>resuelto</c> o <c>ambiguo</c>; cualquier otro (o vacío), <c>sin_resolver</c>.</summary>
    private static string Estado(string? estado) =>
        estado is clsCamposNormalizacion.Resuelto or clsCamposNormalizacion.Ambiguo ? estado : clsCamposNormalizacion.SinResolver;

    /// <summary>La confianza de NEXO acotada a [0, 1] (un valor no numérico, 0).</summary>
    private static double Confianza(double confianza) => double.IsNaN(confianza) ? 0 : Math.Clamp(confianza, 0, 1);

    private static clsValorNormalizacionWeb SinResolver(string campo, string valor, string? origen, string? nota) =>
        new(campo, valor, clsCamposNormalizacion.SinResolver, null, null, null,
            string.IsNullOrWhiteSpace(origen) ? clsCamposNormalizacion.OrigenNinguno : origen, 0, nota, null, null, null);

    /// <summary>Clave para emparejar con NEXO: sin tildes, mayúsculas ni signos (solo letras y dígitos).</summary>
    private static string Clave(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var clave = new StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                clave.Append(char.ToLowerInvariant(c));
            }
        }

        return clave.ToString();
    }
}
