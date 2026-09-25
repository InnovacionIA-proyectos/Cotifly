using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Pdf;

/// <summary>Pasos de <c>specs/features/pdf.feature</c> (y los de generación que reutiliza <c>auditoria.feature</c>).</summary>
[Binding]
public sealed class clsPasosPdf(clsContextoEscenario contexto)
{
    private byte[]? _primerPdf;

    [Given("el comercial {string} con nombre {string} y correo {string}")]
    public async Task DadoElComercial(string nombreUsuario, string nombre, string correo)
    {
        clsUsuarioWeb? usuario = null;
        await contexto.EnAmbitoAsync(async s =>
        {
            var usuarios = s.GetRequiredService<IClsUsuarioWebRepository>();
            usuario = await usuarios.ObtenerPorNombreUsuarioAsync(nombreUsuario);
            if (usuario is null)
            {
                usuario = clsUsuarioWeb.Crear(nombreUsuario, nombre, correo, null, contexto.Reloj.AhoraUtc).Valor;
                usuarios.Agregar(usuario);
            }

            usuario.ActualizarDatos(nombre, correo, null);
            usuario.CambiarTelefono(null); // cada escenario arranca sin teléfono (la base es compartida)
        });
        contexto.Usuarios[nombreUsuario] = usuario!.Id;
    }

    [Given("que {string} tiene el teléfono {string}")]
    [When("{string} confirma el teléfono {string}")]
    public async Task DadoElTelefono(string nombreUsuario, string telefono)
    {
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsTelefonoComercialService>().GuardarAsync(Usuario(nombreUsuario), telefono));
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
    }

    [When("{string} genera el PDF de la solicitud de ejemplo con el folio {string}")]
    public Task CuandoGeneraElEjemplo(string nombreUsuario, string folio) => GenerarAsync(nombreUsuario, Ejemplo(), folio);

    [When("{string} genera el PDF de la solicitud de ejemplo con el folio {string} en el hilo {string}")]
    public Task CuandoGeneraElEjemploEnElHilo(string nombreUsuario, string folio, string hilo) => GenerarAsync(nombreUsuario, Ejemplo(), folio, hilo);

    [Given("que {string} generó el PDF de la solicitud de ejemplo con el folio {string}")]
    public async Task DadoQueGenero(string nombreUsuario, string folio)
    {
        await GenerarAsync(nombreUsuario, Ejemplo(), folio);
        contexto.UltimoErrorNegocio.Should().BeNull();
        _primerPdf = contexto.UltimoPdf;
    }

    [When("{string} genera el PDF de la línea {string} con aduana en frontera {string} y el folio {string}")]
    public Task CuandoGeneraConAduana(string nombreUsuario, string linea, string frontera, string folio)
    {
        var payload = Ejemplo();
        payload.LineaNegocio = linea;
        payload.Aduana = new clsAduanaPayload { Modalidad = "DTAI", Frontera = frontera, Arancel = "5%" };
        return GenerarAsync(nombreUsuario, payload, folio);
    }

    [When("{string} genera el PDF con las secciones {string} y {string} y el folio {string}")]
    public Task CuandoGeneraMulti(string nombreUsuario, string primera, string segunda, string folio)
    {
        var payload = Ejemplo();
        payload.Tarifa = null;
        payload.Secciones =
        [
            new clsTarifaPayload { Titulo = primera, Origen = "YUMBO", Filas = [new clsFilaTarifaPayload { Destino = "BUN", VehiculoPeso = "Tractomula", Tarifa = 3_000_000m }] },
            new clsTarifaPayload { Titulo = segunda, Origen = "BOGOTA", Filas = [new clsFilaTarifaPayload { Destino = "MED", VehiculoPeso = "Sencillo", Tarifa = 1_200_000m }] },
        ];
        return GenerarAsync(nombreUsuario, payload, folio);
    }

    [When("{string} genera el PDF con el servicio conexo {string} por {int} y el folio {string}")]
    public Task CuandoGeneraConServicio(string nombreUsuario, string servicio, int precio, string folio)
    {
        var payload = Ejemplo();
        payload.ServiciosConexos = [new clsServicioConexoPayload { Descripcion = servicio, Precio = precio }];
        return GenerarAsync(nombreUsuario, payload, folio);
    }

    [When("{string} genera el PDF de la línea {string} con el folio {string}")]
    public Task CuandoGeneraDeLaLinea(string nombreUsuario, string linea, string folio) => CuandoGeneraDeLaLineaEnElHilo(nombreUsuario, linea, folio, null);

    [When("{string} genera el PDF de la línea {string} con el folio {string} en el hilo {string}")]
    public Task CuandoGeneraDeLaLineaEnElHilo(string nombreUsuario, string linea, string folio, string? hilo)
    {
        var payload = Ejemplo();
        payload.LineaNegocio = linea;
        return GenerarAsync(nombreUsuario, payload, folio, hilo);
    }

    [When("{string} genera el PDF para el cliente {string} con el folio {string}")]
    public Task CuandoGeneraParaElCliente(string nombreUsuario, string cliente, string folio)
    {
        var payload = Ejemplo();
        payload.Cliente = cliente;
        return GenerarAsync(nombreUsuario, payload, folio);
    }

    [Then("la cotización {string} queda registrada de {string} con la línea {string} y estado de envío {string}")]
    public async Task EntoncesQuedaRegistrada(string folio, string nombreUsuario, string linea, string estado)
    {
        contexto.UltimoErrorNegocio.Should().BeNull();
        var (cotizacion, lineaLeida) = await contexto.ConsultarAsync(async s =>
        {
            var leida = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio);
            var politica = await s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorIdAsync(leida!.LineaNegocioId!.Value);
            return (leida, politica?.LineaNegocio);
        });

        cotizacion.UsuarioId.Should().Be(Usuario(nombreUsuario));
        lineaLeida.Should().Be(linea);
        clsCodigoEnum.ACodigo(cotizacion.EstadoEnvio!.Value).Should().Be(estado);
        cotizacion.PdfPath.Should().EndWith(folio + ".pdf");
    }

    [Then("el PDF tiene {int} páginas")]
    public void EntoncesTienePaginas(int paginas) => Lector().Paginas.Should().HaveCount(paginas);

    [Then("el PDF contiene {string}")]
    public void EntoncesContiene(string texto) => Lector().Texto.Should().Contain(texto);

    [Then("el PDF no contiene {string}")]
    public void EntoncesNoContiene(string texto) => Lector().Texto.Should().NotContain(texto);

    [Then("el PDF queda en el almacén como {string}")]
    public async Task EntoncesQuedaEnElAlmacen(string nombre)
    {
        var guardado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerAsync(nombre));
        guardado.Should().Equal(contexto.UltimoPdf);
    }

    [Then("la generación se rechaza con el código {string}")]
    public void EntoncesSeRechaza(string codigo)
    {
        contexto.UltimoErrorNegocio.Should().NotBeNull("se esperaba el rechazo {0}", codigo);
        contexto.UltimoErrorNegocio!.Codigo.Should().Be(codigo);
    }

    [Then("el PDF {string} del almacén es el de la primera generación")]
    public async Task EntoncesEsElDeLaPrimera(string nombre)
    {
        var guardado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerAsync(nombre));
        guardado.Should().Equal(_primerPdf);
    }

    [Then("no hay PDF {string} en el almacén")]
    public async Task EntoncesNoHayPdf(string nombre) =>
        (await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerAsync(nombre))).Should().BeNull();

    [Then("el teléfono de {string} es {string}")]
    public async Task EntoncesElTelefonoEs(string nombreUsuario, string telefono)
    {
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsTelefonoComercialService>().ObtenerAsync(Usuario(nombreUsuario)));
        resultado.Valor.Telefono.Should().Be(telefono);
    }

    private async Task GenerarAsync(string nombreUsuario, clsCotizacionPdfPayload payload, string folio, string? hilo = null)
    {
        var entrada = new clsEntradaGenerarPdf(Usuario(nombreUsuario), payload, folio, SpaceId: hilo, ThreadId: hilo);
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsGenerarPdfService>().GenerarAsync(entrada));
        contexto.UltimoErrorNegocio = resultado.EsFallo ? resultado.Error : null;
        contexto.UltimoPdf = resultado.EsExitoso
            ? await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerAsync(folio + ".pdf"))
            : null;
    }

    private clsLectorPdf Lector()
    {
        contexto.UltimoErrorNegocio.Should().BeNull();
        contexto.UltimoPdf.Should().NotBeNull();
        return clsLectorPdf.Leer(contexto.UltimoPdf!);
    }

    private static clsCotizacionPdfPayload Ejemplo() =>
        clsCotizacionPdfPayload.DesdeJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "cotizacion", "ejemplo-solicitud.json"))).Valor;

    private Guid Usuario(string nombreUsuario) =>
        contexto.Usuarios.TryGetValue(nombreUsuario, out var id)
            ? id
            : throw new InvalidOperationException($"El comercial '{nombreUsuario}' no está registrado en el escenario.");
}
