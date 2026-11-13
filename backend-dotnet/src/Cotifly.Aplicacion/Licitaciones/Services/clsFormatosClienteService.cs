using Cotifly.Aplicacion.Comun;
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
/// <para><b>Diccionario: lo que se ve es lo que se edita.</b> Con NIT se muestran los términos de ese cliente más los generales; sin NIT,
/// solo los generales (NEXO devolvería los de todos los clientes). Los términos se crean, editan y borran solo para un cliente (NIT
/// obligatorio): los generales no se tocan desde Cotifly (<c>TERMINO_GENERAL_NO_EDITABLE</c>) y un id que no aparece entre los del cliente
/// es <c>TERMINO_NO_ENCONTRADO</c>, sin llamar a la escritura de NEXO.</para>
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
    /// <c>GET diccionario-cliente</c> con los filtros <paramref name="tipo"/> y <paramref name="q"/>: con <paramref name="nitCliente"/>, los
    /// términos de ese cliente más los generales; sin él, solo los generales.
    /// </summary>
    Task<clsResultado<clsConsultaDiccionarioCliente>> ConsultarDiccionarioAsync(
        Guid usuarioId, string? nitCliente, string? tipo, string? q, CancellationToken cancellationToken = default);

    /// <summary><c>POST diccionario-cliente</c>: valida los campos y el NIT antes de NEXO y crea el término del cliente.</summary>
    Task<clsResultado<clsTerminoCliente>> CrearTerminoAsync(
        Guid usuarioId, clsEntradaTerminoCliente entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>PUT diccionario-cliente/{id}</c>: valida los campos y el NIT, comprueba que el término sea del cliente (no general) y lo edita.
    /// </summary>
    Task<clsResultado<clsTerminoCliente>> EditarTerminoAsync(
        Guid usuarioId, Guid terminoId, clsEntradaTerminoCliente entrada, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE diccionario-cliente/{id}</c>: comprueba que el término sea del cliente del NIT (no general) y lo borra.</summary>
    Task<clsResultado<bool>> EliminarTerminoAsync(Guid usuarioId, Guid terminoId, string? nitCliente, CancellationToken cancellationToken = default);

    /// <summary><c>GET diccionario-cliente/opciones</c>: tipos, campos de NEXO, unidades y contextos.</summary>
    Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IClsFormatosClienteService"/>
public sealed partial class clsFormatosClienteService(IClsImportacionExcelNexo importacion, ILogger<clsFormatosClienteService> logger)
    : IClsFormatosClienteService
{
    /// <summary>Largo máximo del <c>termino</c> que acepta NEXO.</summary>
    public const int LargoMaximoTermino = 200;

    /// <summary>Largo máximo del <c>tipo</c>.</summary>
    public const int LargoMaximoTipo = 100;

    /// <summary>Largo máximo del <c>concepto</c> que acepta NEXO.</summary>
    public const int LargoMaximoConcepto = 100;

    /// <summary>Largo máximo del <c>contexto</c> (el nombre de un campo de NEXO).</summary>
    public const int LargoMaximoContexto = 50;

    /// <summary>Largo máximo del <c>ejemplo</c> que acepta NEXO.</summary>
    public const int LargoMaximoEjemplo = 500;

    public const string MensajeTerminoRequerido = "Escribe el término tal como lo usa el cliente.";

    public const string MensajeTipoRequerido = "Elige el tipo del término: columna, unidad o valor.";

    public const string MensajeConceptoRequerido = "Indica qué significa el término para NEXO.";

    public const string MensajeTerminoNoEncontrado = "El término no existe o no es de este cliente.";

    public const string MensajeTerminoGeneral =
        "Este término es general (aplica a todos los clientes): no se puede editar ni borrar desde Cotifly.";

    /// <summary>
    /// NIT de la consulta de control del diccionario: letras que ningún cliente ni prospecto tiene como NIT. Con él, un NEXO que filtra por
    /// NIT no devuelve ningún término de cliente.
    /// </summary>
    public const string NitDeControl = "COTIFLYSINCLIENTE";

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
        var terminos = await VisiblesAsync(
            usuarioId, nit, new clsFiltroDiccionarioCliente(tipo, q, SoloActivos: false, IncluirGenerales: true), cancellationToken);
        return terminos.EsFallo ? terminos.Error : new clsConsultaDiccionarioCliente(terminos.Valor);
    }

    public async Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(
        Guid usuarioId, CancellationToken cancellationToken = default) =>
        SinComercial<clsOpcionesDiccionarioCliente>(usuarioId) ?? await importacion.ObtenerOpcionesDiccionarioAsync(usuarioId, cancellationToken);

    public async Task<clsResultado<clsTerminoCliente>> CrearTerminoAsync(
        Guid usuarioId, clsEntradaTerminoCliente entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if ((SinComercial<clsTerminoCliente>(usuarioId) ?? SinNit<clsTerminoCliente>(entrada.NitCliente)) is { } error)
        {
            return error;
        }

        if (Validar(entrada, out var datos) is { } invalido)
        {
            return invalido;
        }

        var creado = await importacion.CrearTerminoClienteAsync(usuarioId, entrada.NitCliente!, datos, cancellationToken);
        if (creado.EsExitoso)
        {
            LogTerminoCreado(logger, usuarioId, creado.Valor.Id);
        }

        return creado;
    }

    public async Task<clsResultado<clsTerminoCliente>> EditarTerminoAsync(
        Guid usuarioId, Guid terminoId, clsEntradaTerminoCliente entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if ((SinComercial<clsTerminoCliente>(usuarioId) ?? SinNit<clsTerminoCliente>(entrada.NitCliente)) is { } error)
        {
            return error;
        }

        if (Validar(entrada, out var datos) is { } invalido)
        {
            return invalido;
        }

        if (await TerminoDelClienteAsync(usuarioId, terminoId, entrada.NitCliente!, cancellationToken) is { } noEditable)
        {
            return noEditable;
        }

        var editado = await importacion.EditarTerminoClienteAsync(usuarioId, terminoId, datos, cancellationToken);
        if (editado.EsExitoso)
        {
            LogTerminoEditado(logger, usuarioId, terminoId);
        }

        return editado;
    }

    public async Task<clsResultado<bool>> EliminarTerminoAsync(
        Guid usuarioId, Guid terminoId, string? nitCliente, CancellationToken cancellationToken = default)
    {
        if ((SinComercial<bool>(usuarioId) ?? SinNit<bool>(nitCliente)) is { } error)
        {
            return error;
        }

        if (await TerminoDelClienteAsync(usuarioId, terminoId, nitCliente!, cancellationToken) is { } noEditable)
        {
            return noEditable;
        }

        var borrado = await importacion.EliminarTerminoClienteAsync(usuarioId, terminoId, cancellationToken);
        if (borrado.EsExitoso)
        {
            LogTerminoBorrado(logger, usuarioId, terminoId);
        }

        return borrado;
    }

    /// <summary>
    /// Los términos que Cotifly puede mostrar (y editar). Sin NIT, NEXO responde los de todos los clientes: solo salen los generales. Con NIT,
    /// los generales y los del cliente, pero solo si está demostrado que NEXO filtró por el NIT; si no, solo los generales (falla cerrado).
    /// </summary>
    /// <remarks>
    /// Los términos de NEXO no traen el NIT de su cliente (solo su <c>clienteId</c> local, que Cotifly no puede cruzar con el NIT), así que la
    /// pertenencia se demuestra por el comportamiento de NEXO en la misma consulta:
    /// <list type="bullet">
    /// <item>la respuesta no puede traer términos de más de un cliente;</item>
    /// <item>una consulta de control con un NIT que no es de ningún cliente (<see cref="NitDeControl"/>) y sin generales debe volver sin
    /// términos de cliente. Si NEXO ignorara el NIT, devolvería ahí los mismos términos ajenos que en la consulta del comercial.</item>
    /// </list>
    /// Un término sin <c>clienteId</c> leído nunca sale. Si la consulta de control falla, falla toda la operación.
    /// </remarks>
    private async Task<clsResultado<IReadOnlyList<clsTerminoCliente>>> VisiblesAsync(
        Guid usuarioId, string? nit, clsFiltroDiccionarioCliente filtro, CancellationToken cancellationToken)
    {
        var terminos = await importacion.ObtenerDiccionarioClienteAsync(usuarioId, nit, filtro, cancellationToken);
        if (terminos.EsFallo)
        {
            return terminos.Error;
        }

        IReadOnlyList<clsTerminoCliente> generales = [.. terminos.Valor.Where(t => t.EsGeneral)];
        var delCliente = terminos.Valor.Where(EsDeUnCliente).ToList();
        if (nit is null || delCliente.Count == 0)
        {
            return clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito(generales);
        }

        var clientes = delCliente.Select(t => t.ClienteId).Distinct().Count();
        if (clientes > 1)
        {
            LogDiccionarioSinFiltrar(logger, clientes);
            return clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito(generales);
        }

        var control = await importacion.ObtenerDiccionarioClienteAsync(
            usuarioId, NitDeControl, new clsFiltroDiccionarioCliente(SoloActivos: false, IncluirGenerales: false), cancellationToken);
        if (control.EsFallo)
        {
            return control.Error;
        }

        if (control.Valor.Any(t => !t.EsGeneral))
        {
            LogDiccionarioSinFiltroPorNit(logger);
            return clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito(generales);
        }

        return clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito([.. terminos.Valor.Where(t => t.EsGeneral || EsDeUnCliente(t))]);
    }

    private static bool EsDeUnCliente(clsTerminoCliente termino) => termino.ClienteIdLeido && termino.ClienteId is not null;

    /// <summary>
    /// <c>null</c> si <paramref name="terminoId"/> está entre los términos del cliente del NIT. Si es general, <c>TERMINO_GENERAL_NO_EDITABLE</c>;
    /// si no aparece (no existe, es de otro cliente o NEXO no filtró), <c>TERMINO_NO_ENCONTRADO</c>. Un fallo de NEXO al listar llega tal cual.
    /// </summary>
    private async Task<clsErrorDominio?> TerminoDelClienteAsync(Guid usuarioId, Guid terminoId, string nitCliente, CancellationToken cancellationToken)
    {
        var lista = await VisiblesAsync(
            usuarioId, nitCliente, new clsFiltroDiccionarioCliente(SoloActivos: false, IncluirGenerales: true), cancellationToken);
        if (lista.EsFallo)
        {
            return lista.Error;
        }

        var termino = lista.Valor.FirstOrDefault(t => t.Id == terminoId);
        return termino switch
        {
            null => new clsErrorDominio(clsCodigosError.TerminoNoEncontrado, MensajeTerminoNoEncontrado),
            { EsGeneral: true } => new clsErrorDominio(clsCodigosError.TerminoGeneralNoEditable, MensajeTerminoGeneral),
            _ => null,
        };
    }

    /// <summary>
    /// Las validaciones locales del término, antes de NEXO: vacíos con los códigos de NEXO (<c>TERMINO_REQUERIDO</c>,
    /// <c>TIPO_TERMINO_INVALIDO</c>, <c>CONCEPTO_REQUERIDO</c>) y largos de más como <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>). Los
    /// textos viajan sin espacios en los extremos y un contexto o ejemplo en blanco viaja como ausente.
    /// </summary>
    private static clsErrorDominio? Validar(clsEntradaTerminoCliente entrada, out clsDatosTerminoCliente datos)
    {
        var termino = entrada.Termino?.Trim() ?? string.Empty;
        var tipo = entrada.Tipo?.Trim() ?? string.Empty;
        var concepto = entrada.Concepto?.Trim() ?? string.Empty;
        var contexto = string.IsNullOrWhiteSpace(entrada.Contexto) ? null : entrada.Contexto.Trim();
        var ejemplo = string.IsNullOrWhiteSpace(entrada.Ejemplo) ? null : entrada.Ejemplo.Trim();
        datos = new clsDatosTerminoCliente(termino, tipo, concepto, contexto, ejemplo, entrada.Activo ?? true);

        if (termino.Length == 0)
        {
            return new clsErrorDominio(clsCodigosError.TerminoRequerido, MensajeTerminoRequerido);
        }

        if (tipo.Length == 0)
        {
            return new clsErrorDominio(clsCodigosError.TipoTerminoInvalido, MensajeTipoRequerido);
        }

        if (concepto.Length == 0)
        {
            return new clsErrorDominio(clsCodigosError.ConceptoRequerido, MensajeConceptoRequerido);
        }

        return LargoDeMas("termino", termino, LargoMaximoTermino) ?? LargoDeMas("tipo", tipo, LargoMaximoTipo)
            ?? LargoDeMas("concepto", concepto, LargoMaximoConcepto) ?? LargoDeMas("contexto", contexto, LargoMaximoContexto)
            ?? LargoDeMas("ejemplo", ejemplo, LargoMaximoEjemplo);
    }

    private static clsErrorDominio? LargoDeMas(string campo, string? valor, int maximo) =>
        valor is not null && valor.Length > maximo
            ? new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida, $"El campo {campo} admite hasta {maximo} caracteres.")
            : null;

    // #256 (B1): el borrado también deja rastro en Cotifly (además de la auditoría de NEXO), para soporte cuando un formato "desaparece".
    // Solo el comercial y el id: ni el NIT ni el nombre del cliente.
    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} borró en NEXO la plantilla de mapeo {PlantillaId}.")]
    private static partial void LogPlantillaBorrada(ILogger logger, Guid usuarioId, Guid plantillaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} borró en NEXO el formato de lectura {RecetaId}.")]
    private static partial void LogRecetaBorrada(ILogger logger, Guid usuarioId, Guid recetaId);

    // Los cambios del diccionario dejan el mismo rastro: el comercial y el id del término, sin el NIT ni el texto del término.
    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} creó en NEXO el término del diccionario {TerminoId}.")]
    private static partial void LogTerminoCreado(ILogger logger, Guid usuarioId, Guid terminoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} editó en NEXO el término del diccionario {TerminoId}.")]
    private static partial void LogTerminoEditado(ILogger logger, Guid usuarioId, Guid terminoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "El comercial {UsuarioId} borró en NEXO el término del diccionario {TerminoId}.")]
    private static partial void LogTerminoBorrado(ILogger logger, Guid usuarioId, Guid terminoId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NEXO respondió el diccionario con términos de {Clientes} clientes al pedirlo por NIT: solo se muestran los generales.")]
    private static partial void LogDiccionarioSinFiltrar(ILogger logger, int clientes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NEXO respondió términos de cliente a la consulta de control del diccionario (no filtra por NIT): solo se muestran los generales.")]
    private static partial void LogDiccionarioSinFiltroPorNit(ILogger logger);

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
