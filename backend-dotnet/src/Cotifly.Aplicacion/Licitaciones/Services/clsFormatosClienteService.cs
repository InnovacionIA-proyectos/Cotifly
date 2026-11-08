using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// HU-044.5 (#256): lo que NEXO recuerda de cada cliente para la importación del Excel de licitación. Las <b>plantillas de mapeo</b>
/// (<c>/web/licitaciones/plantillas-mapeo</c>), los <b>formatos de lectura</b> o recetas (<c>/web/licitaciones/recetas-lectura</c>) y la
/// consulta del <b>diccionario del cliente</b> (<c>/web/diccionario-cliente</c>). Cotifly es solo el puente
/// (<see cref="IClsImportacionExcelNexo"/>, ADR-028): no guarda copia y la auditoría la lleva NEXO.
/// </summary>
/// <remarks>
/// <para><b>Los listados exigen el NIT.</b> Sin él NEXO devolvería las plantillas o los formatos de TODOS los clientes: responde
/// <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.</para>
/// <para><b>El diccionario es de solo lectura y solo con los términos generales.</b> El NEXO de QA no filtra el diccionario por
/// <c>clienteNit</c> (devuelve los términos de todos los clientes), así que Cotifly descarta todo término con cliente. Con NIT, además, avisa
/// que los términos propios del cliente aún no están disponibles. La escritura (<c>POST/PUT/DELETE</c>) espera el contrato §3.19.1 de NEXO.</para>
/// </remarks>
public interface IClsFormatosClienteService
{
    /// <summary><c>PUT plantillas-mapeo</c>: guarda o reemplaza la plantilla del cliente (<see cref="clsPlantillaGuardada.Creada"/>: 201 o 200).</summary>
    Task<clsResultado<clsPlantillaGuardada>> GuardarPlantillaAsync(
        Guid usuarioId, clsPeticionPlantillaMapeo peticion, CancellationToken cancellationToken = default);

    /// <summary><c>GET plantillas-mapeo</c>: las del cliente. Sin NIT, <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.</summary>
    Task<clsResultado<IReadOnlyList<clsPlantillaMapeo>>> ListarPlantillasAsync(
        Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE plantillas-mapeo/{id}</c>. <c>PLANTILLA_NO_ENCONTRADA</c> si NEXO no la tiene.</summary>
    Task<clsResultado<bool>> EliminarPlantillaAsync(Guid usuarioId, Guid plantillaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>PUT recetas-lectura</c>: guarda el formato confirmado. Antes de NEXO solo valida el archivo (presente, tamaño y extensión, sin abrir
    /// el libro, #277), que venga la receta (<c>RECETA_REQUERIDA</c>) y el NIT. Nunca se reintenta: NEXO ejecuta la receta al guardarla.
    /// </summary>
    Task<clsResultado<clsRecetaGuardadaResultado>> GuardarRecetaAsync(
        Guid usuarioId, clsEntradaGuardarReceta entrada, CancellationToken cancellationToken = default);

    /// <summary><c>GET recetas-lectura</c>: los formatos del cliente. Sin NIT, <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.</summary>
    Task<clsResultado<IReadOnlyList<clsRecetaGuardada>>> ListarRecetasAsync(
        Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE recetas-lectura/{id}</c>. <c>RECETA_NO_ENCONTRADA</c> si NEXO no lo tiene.</summary>
    Task<clsResultado<bool>> EliminarRecetaAsync(Guid usuarioId, Guid recetaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET diccionario-cliente</c>: SOLO los términos generales (<c>ClienteId == null</c>) con los filtros <paramref name="tipo"/> y
    /// <paramref name="q"/>. Con <paramref name="nitCliente"/>, además, el aviso <see cref="clsFormatosClienteService.AvisoTerminosClienteNoDisponibles"/>.
    /// </summary>
    Task<clsResultado<clsConsultaDiccionarioCliente>> ConsultarDiccionarioAsync(
        Guid usuarioId, string? nitCliente, string? tipo, string? q, CancellationToken cancellationToken = default);

    /// <summary><c>GET diccionario-cliente/opciones</c>: tipos, campos de NEXO, unidades y contextos.</summary>
    Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IClsFormatosClienteService"/>
public sealed partial class clsFormatosClienteService(IClsImportacionExcelNexo importacion, ILogger<clsFormatosClienteService> logger)
    : IClsFormatosClienteService
{
    /// <summary>Código del aviso del diccionario consultado con NIT: por ahora solo se ven los términos generales.</summary>
    public const string AvisoTerminosClienteNoDisponibles = "TERMINOS_CLIENTE_NO_DISPONIBLES";

    public const string MensajeTerminosClienteNoDisponibles =
        "Por ahora solo se muestran los términos generales: los términos propios del cliente todavía no se pueden consultar desde Cotifly.";

    public const string MensajeClienteRequerido = "Indica el NIT del cliente de la licitación.";

    public const string MensajeRecetaRequerida = "Envía la receta de la lectura que confirmaste.";

    public Task<clsResultado<clsPlantillaGuardada>> GuardarPlantillaAsync(
        Guid usuarioId, clsPeticionPlantillaMapeo peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        return SinComercial<clsPlantillaGuardada>(usuarioId) is { } error
            ? Task.FromResult(error)
            : importacion.GuardarPlantillaMapeoAsync(usuarioId, peticion, cancellationToken);
    }

    public async Task<clsResultado<IReadOnlyList<clsPlantillaMapeo>>> ListarPlantillasAsync(
        Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default) =>
        SinComercial<IReadOnlyList<clsPlantillaMapeo>>(usuarioId) ?? SinNit<IReadOnlyList<clsPlantillaMapeo>>(nitCliente)
        ?? await importacion.ListarPlantillasMapeoAsync(usuarioId, nitCliente!, cancellationToken);

    public async Task<clsResultado<bool>> EliminarPlantillaAsync(Guid usuarioId, Guid plantillaId, CancellationToken cancellationToken = default)
    {
        if (SinComercial<bool>(usuarioId) is { } sinComercial)
        {
            return sinComercial;
        }

        var resultado = await importacion.EliminarPlantillaMapeoAsync(usuarioId, plantillaId, cancellationToken);
        if (resultado.EsExitoso)
        {
            LogPlantillaBorrada(logger, usuarioId, plantillaId);
        }

        return resultado;
    }

    public async Task<clsResultado<clsRecetaGuardadaResultado>> GuardarRecetaAsync(
        Guid usuarioId, clsEntradaGuardarReceta entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Archivo);
        if (SinComercial<clsRecetaGuardadaResultado>(usuarioId) is { } sinComercial)
        {
            return sinComercial;
        }

        // Cotifly no abre el libro (#277): solo presencia, tamaño y extensión. Lo demás (la receta contra el archivo) lo decide NEXO.
        if (clsArchivoExcel.ValidarArchivo(entrada.Archivo.Nombre, entrada.Archivo.Contenido) is { } invalido)
        {
            return invalido;
        }

        // Sin receta no se sube un Excel de hasta 10 MB para que NEXO responda lo mismo.
        if (string.IsNullOrWhiteSpace(entrada.Receta))
        {
            return new clsErrorDominio(clsCodigosError.RecetaRequerida, MensajeRecetaRequerida);
        }

        if (SinNit<clsRecetaGuardadaResultado>(entrada.NitCliente) is { } sinNit)
        {
            return sinNit;
        }

        var archivo = new clsArchivoImportacion(clsArchivoExcel.NombreLimpio(entrada.Archivo.Nombre), entrada.Archivo.Contenido);
        return await importacion.GuardarRecetaLecturaAsync(
            usuarioId, new clsPeticionGuardarReceta(archivo, entrada.Receta, entrada.NitCliente!, entrada.Nombre, entrada.Instrucciones), cancellationToken);
    }

    public async Task<clsResultado<IReadOnlyList<clsRecetaGuardada>>> ListarRecetasAsync(
        Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default) =>
        SinComercial<IReadOnlyList<clsRecetaGuardada>>(usuarioId) ?? SinNit<IReadOnlyList<clsRecetaGuardada>>(nitCliente)
        ?? await importacion.ListarRecetasLecturaAsync(usuarioId, nitCliente!, cancellationToken);

    public async Task<clsResultado<bool>> EliminarRecetaAsync(Guid usuarioId, Guid recetaId, CancellationToken cancellationToken = default)
    {
        if (SinComercial<bool>(usuarioId) is { } sinComercial)
        {
            return sinComercial;
        }

        var resultado = await importacion.EliminarRecetaLecturaAsync(usuarioId, recetaId, cancellationToken);
        if (resultado.EsExitoso)
        {
            LogRecetaBorrada(logger, usuarioId, recetaId);
        }

        return resultado;
    }

    public async Task<clsResultado<clsConsultaDiccionarioCliente>> ConsultarDiccionarioAsync(
        Guid usuarioId, string? nitCliente, string? tipo, string? q, CancellationToken cancellationToken = default)
    {
        if (SinComercial<clsConsultaDiccionarioCliente>(usuarioId) is { } sinComercial)
        {
            return sinComercial;
        }

        var nit = string.IsNullOrWhiteSpace(nitCliente) ? null : nitCliente;
        var terminos = await importacion.ObtenerDiccionarioClienteAsync(
            usuarioId, nit, new clsFiltroDiccionarioCliente(tipo, q, SoloActivos: false, IncluirGenerales: true), cancellationToken);
        if (terminos.EsFallo)
        {
            return terminos.Error;
        }

        // NEXO QA (2026-11-06) ignora clienteNit en este GET y responde los términos de todos los clientes: un término con cliente nunca
        // sale de Cotifly, ni siquiera con el NIT (ClienteId es el id de NEXO, que Cotifly no puede cruzar con el NIT; ADR-028).
        // #256 (B2): falla cerrado: un término sin clienteId explícito (cambio de forma de NEXO) o con nombre de cliente no es general.
        IReadOnlyList<clsTerminoCliente> generales = [.. terminos.Valor.Where(t => t.EsGeneral)];
        var aviso = nit is null ? null : new clsAvisoDiccionario(AvisoTerminosClienteNoDisponibles, MensajeTerminosClienteNoDisponibles);
        return new clsConsultaDiccionarioCliente(generales, aviso);
    }

    public async Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(
        Guid usuarioId, CancellationToken cancellationToken = default) =>
        SinComercial<clsOpcionesDiccionarioCliente>(usuarioId) ?? await importacion.ObtenerOpcionesDiccionarioAsync(usuarioId, cancellationToken);

    // #256 (B1): el borrado también deja rastro en Cotifly (además de la auditoría de NEXO), para soporte cuando un formato "desaparece".
    // Solo el comercial y el id: ni el NIT ni el nombre del cliente.
    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} borró en NEXO la plantilla de mapeo {PlantillaId}.")]
    private static partial void LogPlantillaBorrada(ILogger logger, Guid usuarioId, Guid plantillaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} borró en NEXO el formato de lectura {RecetaId}.")]
    private static partial void LogRecetaBorrada(ILogger logger, Guid usuarioId, Guid recetaId);

    /// <summary><c>ERROR_INTERNO</c> si no hay comercial (la política lo garantiza: sería un error de Cotifly, nunca del cliente).</summary>
    private static clsResultado<T>? SinComercial<T>(Guid usuarioId) =>
        usuarioId == Guid.Empty
            ? clsResultado<T>.Fallo(new clsErrorDominio(clsCodigosError.ErrorInterno, "Los formatos del cliente exigen el comercial autenticado."))
            : null;

    /// <summary><c>CLIENTE_REQUERIDO</c> sin NIT: sin él NEXO devolvería o usaría los datos de todos los clientes.</summary>
    private static clsResultado<T>? SinNit<T>(string? nitCliente) =>
        string.IsNullOrWhiteSpace(nitCliente)
            ? clsResultado<T>.Fallo(new clsErrorDominio(clsCodigosError.ClienteRequerido, MensajeClienteRequerido))
            : null;
}
