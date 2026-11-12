using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging;

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
public sealed class clsSugerenciaMapeoService(
    IClsImportacionExcelNexo importacion,
    IClsColumnasDerivadasService columnasDerivadas,
    ILogger<clsSugerenciaMapeoService>? logger = null,
    IClsCubicador? cubicador = null) : IClsSugerenciaMapeoService
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

    /// <summary>Largo máximo del NIT normalizado que acepta NEXO (el mismo del adaptador).</summary>
    public const int LargoMaximoNit = 30;

    /// <summary>El mismo mensaje del adaptador para <c>CLIENTE_NIT_INVALIDO</c>.</summary>
    public const string MensajeNitInvalido = "El NIT del cliente de la licitación no es válido.";

    public const string MensajeLecturaDemasiadoGrande =
        "El archivo con la receta y las instrucciones supera el máximo de 9,9 MB (10 MB con el envío) que acepta NEXO: lee el archivo sin la receta o usa un archivo más pequeño.";

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

        // O1 (validador): el NIT se revisa antes que el límite de columnas, para que un NIT inválido no quede escondido en la hoja sin propuesta.
        if (NitInvalido(peticion.ClienteNit))
        {
            return new clsErrorDominio(clsCodigosError.ClienteNitInvalido, MensajeNitInvalido);
        }

        // HU-044.9 (#292): antes de llamar a NEXO, Cotifly agrega las columnas derivadas (vehículo en el texto, contenedor con peso) con
        // nombre fijo al final. Se enriquece la muestra que de verdad viaja a NEXO (hasta 200 filas), para que la propuesta incluya esas
        // columnas y sus índices coincidan con los que agregará aplicar-mapeo. Un archivo sin esos textos vuelve con su misma firma.
        IReadOnlyList<IReadOnlyList<string>> muestra = [.. (peticion.Filas ?? []).Take(MaximoFilasMuestra)];
        var enriquecida = await columnasDerivadas.EnriquecerAsync(usuarioId, encabezados, muestra, cancellationToken);
        encabezados = enriquecida.Encabezados;

        // NEXO rechaza más de 100 columnas: no se le llama y la hoja sigue en manual (no es un error para el comercial).
        if (encabezados.Count > MaximoEncabezados)
        {
            return SinPropuesta(encabezados, clsCodigosError.DemasiadasColumnas);
        }

        var sugerencia = await importacion.SugerirMapeoAsync(usuarioId, new clsPeticionSugerirMapeo(peticion.ClienteNit, encabezados, enriquecida.Filas), cancellationToken);

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

        // Auditoría B7: el tope de NEXO es del envío completo. El archivo cabe solo, pero con la receta (hasta 60.000 caracteres) y las
        // instrucciones puede no caber: se dice aquí con su propio código, sin llamar a NEXO y sin culpar al archivo.
        if (entrada.Archivo.Contenido.LongLength + Bytes(entrada.Receta) + Bytes(entrada.Instrucciones) + Bytes(entrada.NitCliente)
            > clsArchivoExcel.TamanoMaximoBytes)
        {
            return new clsErrorDominio(clsCodigosError.LecturaDemasiadoGrande, MensajeLecturaDemasiadoGrande);
        }

        var archivo = new clsArchivoImportacion(clsArchivoExcel.NombreLimpio(entrada.Archivo.Nombre), entrada.Archivo.Contenido);
        var lectura = await importacion.LeerEstructuraAsync(
            usuarioId, new clsPeticionLeerEstructura(archivo, entrada.NitCliente, entrada.Instrucciones, entrada.Receta), cancellationToken);
        if (lectura.EsFallo)
        {
            return lectura;
        }

        // HU-044.7 (#290): cada tabla pasa por la misma detección de mercancías de analizar-excel (columna de mercancía, columna de código y
        // el código sugerido por fila). La receta y el resto de la respuesta de NEXO no se tocan; filas_excel sigue null en las tablas.
        var registro = logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger<clsSugerenciaMapeoService>.Instance;
        var conMercancias = await clsDetectorMercanciaExcel.ConMercanciasAsync(lectura.Valor.Tablas, cubicador, registro, cancellationToken);
        return conMercancias.Aplicado ? lectura.Valor with { Tablas = conMercancias.Hojas } : lectura.Valor;
    }

    /// <summary>
    /// La regla del NIT del adaptador (<c>clsImportacionExcelNexo.NormalizarNit</c>, como lo compara NEXO): vacío es "sin cliente"; si no, lo que
    /// va antes del primer guion, solo letras y dígitos, debe quedar con 1 a <see cref="LargoMaximoNit"/> caracteres. El adaptador lo vuelve a
    /// validar (y lo normaliza) antes de llamar a NEXO.
    /// </summary>
    private static bool NitInvalido(string? nit)
    {
        if (string.IsNullOrWhiteSpace(nit))
        {
            return false;
        }

        var limpio = nit.Trim().Split('-')[0].Count(char.IsLetterOrDigit);
        return limpio is 0 or > LargoMaximoNit;
    }

    /// <summary>Bytes UTF-8 de un campo del formulario (0 si no vino).</summary>
    private static long Bytes(string? texto) => texto is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(texto);

    /// <summary>La hoja con cada columna sin propuesta (<c>ignorar</c>, confianza 0), <c>ia_usada = false</c> y el motivo.</summary>
    private static clsSugerenciaMapeo SinPropuesta(IReadOnlyList<string> encabezados, string motivo) => new(
        IaUsada: false,
        MotivoSinIa: motivo,
        Columnas: [.. encabezados.Select((e, i) => new clsColumnaMapeada(i, e, CampoIgnorar, TransformacionNinguna, 0, OrigenNinguno, null))],
        Dudas: [],
        PlantillaId: null);
}
