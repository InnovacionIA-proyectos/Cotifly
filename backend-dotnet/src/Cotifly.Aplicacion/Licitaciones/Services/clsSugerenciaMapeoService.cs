using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// HU-044.2 (#253): la propuesta de mapeo de columnas de una hoja (<c>POST /web/licitaciones/sugerir-mapeo</c>) y su paso previo, la
/// lectura con IA de un Excel de formato complejo (<c>POST /web/licitaciones/leer-estructura</c>). Cotifly no replica nada: es el puente
/// hacia la importación inteligente de NEXO (<see cref="IClsImportacionExcelNexo"/>, ADR-028) y no guarda nada.
/// </summary>
public interface IClsSugerenciaMapeoService
{
    /// <summary>
    /// La propuesta de NEXO (plantilla del cliente, luego diccionario, luego IA) con el <c>campo</c> de NEXO. Es una sugerencia: sin ella el
    /// comercial sigue en manual, así que más de <see cref="clsSugerenciaMapeoService.MaximoEncabezados"/> columnas (sin llamar a NEXO), la
    /// importación apagada o NEXO sin respuesta responden la hoja con sus columnas sin propuesta e <c>ia_usada = false</c>. Sin encabezados,
    /// <c>ENCABEZADOS_REQUERIDOS</c>; <c>IA_TOPE_DIARIO</c> y los rechazos de NEXO (NIT, permisos...) son errores.
    /// </summary>
    Task<clsResultado<clsSugerenciaMapeo>> SugerirAsync(Guid usuarioId, clsPeticionSugerirMapeo peticion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reenvía el Excel a <c>leer-estructura</c> de NEXO y devuelve su lectura sin alterar la <c>receta</c> ni las <c>tablas</c>. Antes de
    /// NEXO solo se valida el archivo (presente, tamaño y extensión, <see cref="clsArchivoExcel.ValidarArchivo"/>): Cotifly no abre el libro,
    /// para no rechazar lo que NEXO sí lee (#277). Nunca se reintenta (cada llamada puede gastar IA).
    /// </summary>
    Task<clsResultado<clsLecturaEstructura>> LeerEstructuraAsync(
        Guid usuarioId, clsEntradaLeerEstructura entrada, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>POST /web/licitaciones/leer-estructura</c>: el Excel con su nombre original, el NIT de la licitación (opcional: solo busca el cliente en
/// NEXO, no lo crea), la frase del comercial (<paramref name="Instrucciones"/>) y la <paramref name="Receta"/> de la lectura anterior como
/// texto JSON opaco: llega a NEXO exactamente como vino.
/// </summary>
public sealed record clsEntradaLeerEstructura(clsArchivoSubido Archivo, string? NitCliente, string? Instrucciones, string? Receta);

/// <inheritdoc cref="IClsSugerenciaMapeoService"/>
public sealed class clsSugerenciaMapeoService(IClsImportacionExcelNexo importacion) : IClsSugerenciaMapeoService
{
    /// <summary>Columnas que admite <c>sugerir-mapeo</c> de NEXO; con más, la hoja se mapea a mano (<c>DEMASIADAS_COLUMNAS</c>).</summary>
    public const int MaximoEncabezados = 100;

    /// <summary>Filas de muestra que se reenvían a NEXO (el frontend manda hasta 200; NEXO usa las 20 primeras). Las de más se descartan.</summary>
    public const int MaximoFilasMuestra = 200;

    /// <summary>Campo de NEXO de una columna sin propuesta (el mismo que usa NEXO).</summary>
    public const string CampoIgnorar = "ignorar";

    /// <summary>Transformación de una columna sin propuesta.</summary>
    public const string TransformacionNinguna = "ninguna";

    /// <summary>Origen de una columna sin propuesta.</summary>
    public const string OrigenNinguno = "ninguno";

    public const string MensajeEncabezadosRequeridos = "Envía los encabezados de la hoja.";

    public async Task<clsResultado<clsSugerenciaMapeo>> SugerirAsync(
        Guid usuarioId, clsPeticionSugerirMapeo peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "La sugerencia de mapeo exige el comercial autenticado.");
        }

        var encabezados = peticion.Encabezados;
        if (encabezados.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.EncabezadosRequeridos, MensajeEncabezadosRequeridos);
        }

        // NEXO rechaza más de 100 columnas: no se le llama y la hoja sigue en manual (no es un error para el comercial).
        if (encabezados.Count > MaximoEncabezados)
        {
            return SinPropuesta(encabezados, clsCodigosError.DemasiadasColumnas);
        }

        IReadOnlyList<IReadOnlyList<string>> filas = [.. (peticion.Filas ?? []).Take(MaximoFilasMuestra)];
        var sugerencia = await importacion.SugerirMapeoAsync(usuarioId, new clsPeticionSugerirMapeo(peticion.ClienteNit, encabezados, filas), cancellationToken);

        // Importación apagada, NEXO caído o sin respuesta a tiempo: la hoja sin propuesta (el adaptador ya dejó el motivo en el log).
        return sugerencia.EsFallo && sugerencia.Error.Codigo == clsCodigosError.NexoNoDisponible
            ? SinPropuesta(encabezados, clsCodigosError.NexoNoDisponible)
            : sugerencia;
    }

    public async Task<clsResultado<clsLecturaEstructura>> LeerEstructuraAsync(
        Guid usuarioId, clsEntradaLeerEstructura entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Archivo);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "La lectura del Excel exige el comercial autenticado.");
        }

        if (clsArchivoExcel.ValidarArchivo(entrada.Archivo.Nombre, entrada.Archivo.Contenido) is { } invalido)
        {
            return invalido;
        }

        var archivo = new clsArchivoImportacion(clsArchivoExcel.NombreLimpio(entrada.Archivo.Nombre), entrada.Archivo.Contenido);
        return await importacion.LeerEstructuraAsync(
            usuarioId, new clsPeticionLeerEstructura(archivo, entrada.NitCliente, entrada.Instrucciones, entrada.Receta), cancellationToken);
    }

    /// <summary>La hoja con cada columna sin propuesta (<c>ignorar</c>, confianza 0), <c>ia_usada = false</c> y el motivo.</summary>
    private static clsSugerenciaMapeo SinPropuesta(IReadOnlyList<string> encabezados, string motivo) => new(
        IaUsada: false,
        MotivoSinIa: motivo,
        Columnas: [.. encabezados.Select((e, i) => new clsColumnaMapeada(i, e, CampoIgnorar, TransformacionNinguna, 0, OrigenNinguno, null))],
        Dudas: [],
        PlantillaId: null);
}
