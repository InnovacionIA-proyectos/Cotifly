using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Cotizaciones;

/// <summary>
/// Pasos de <c>specs/features/folio.feature</c> (ADR-011): <see cref="IClsGeneradorFolioService"/> real sobre SQL Server 2019, con
/// el reloj falso del escenario (la fecha del folio sale de <c>IClsCalendarioNegocio</c>, zona America/Bogota).
/// </summary>
[Binding]
public sealed class clsPasosFolio(clsContextoEscenario contexto)
{
    /// <summary>Bogotá no tiene horario de verano: UTC−5 todo el año.</summary>
    private static readonly TimeSpan _desfaseBogota = TimeSpan.FromHours(-5);

    private readonly List<(string Comercial, string Folio)> _creados = [];
    private readonly Dictionary<string, Guid> _ultimaPorComercial = new(StringComparer.OrdinalIgnoreCase);
    private clsErrorDominio? _error;

    [Given("el comercial de folios {string} llamado {string}")]
    public async Task DadoElComercial(string alias, string nombreCompleto)
    {
        var usuario = clsUsuarioWeb.Crear($"{alias}.{Guid.NewGuid():N}"[..40], nombreCompleto, $"{alias}@boterosoto.com.co", null, contexto.Reloj.AhoraUtc).Valor;
        await contexto.EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsUsuarioWebRepository>().Agregar(usuario);
            return Task.CompletedTask;
        });
        contexto.Usuarios[alias] = usuario.Id;
    }

    [Given("que en Bogotá son las {string}")]
    public void DadoLaHoraDeBogota(string fechaHora) =>
        contexto.Reloj.AhoraUtc = new DateTimeOffset(Leer(fechaHora), _desfaseBogota).ToUniversalTime();

    [Given("que el reloj UTC marca {string}")]
    public void DadoElRelojUtc(string fechaHora) => contexto.Reloj.AhoraUtc = new DateTimeOffset(Leer(fechaHora), TimeSpan.Zero);

    [Given("{string} crea una cotización sin NIT")]
    [When("{string} crea una cotización sin NIT")]
    public Task CuandoCreaSinNit(string alias) => CrearAsync(alias, null);

    [Given("{string} crea una cotización con el NIT {string}")]
    [When("{string} crea una cotización con el NIT {string}")]
    public Task CuandoCreaConNit(string alias, string nit) => CrearAsync(alias, nit);

    [When("{string} crea una versión nueva de su última cotización")]
    public Task CuandoCreaVersion(string alias) => CrearVersionAsync(alias, _ultimaPorComercial[alias]);

    [When("{string} crea una versión nueva de la última cotización de {string}")]
    public Task CuandoCreaVersionAjena(string alias, string dueno) => CrearVersionAsync(alias, _ultimaPorComercial[dueno]);

    [When("{string} crea {int} cotizaciones sin NIT al mismo tiempo")]
    public async Task CuandoCreaSimultaneas(string alias, int cantidad)
    {
        var resultados = await Task.WhenAll(Enumerable.Range(0, cantidad).Select(_ => GenerarAsync(contexto.Usuarios[alias], null)));
        foreach (var resultado in resultados)
        {
            Registrar(alias, resultado);
        }
    }

    [Given("que {string} ya tiene la cotización {string}")]
    public Task DadoQueYaTiene(string alias, string folio) =>
        contexto.EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCotizacionRepository>().Agregar(
                clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = contexto.Usuarios[alias] }).Valor);
            return Task.CompletedTask;
        });

    [Then("el folio creado es {string} con versión {int}")]
    public async Task EntoncesElFolio(string folio, int version)
    {
        _error.Should().BeNull(_error?.Mensaje);
        _creados.Should().ContainSingle().Which.Folio.Should().Be(folio);
        (await LeerAsync(folio)).Version.Should().Be(version);
    }

    [Then("los folios creados son:")]
    public void EntoncesLosFolios(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        _error.Should().BeNull(_error?.Mensaje);
        _creados.Should().Equal(tabla.Rows.Select(f => (f["comercial"], f["folio"])));
    }

    [Then("los folios de la familia son {string}, {string} y {string}")]
    public void EntoncesLaFamilia(string v1, string v2, string v3)
    {
        _error.Should().BeNull(_error?.Mensaje);
        _creados.Select(c => c.Folio).Should().Equal(v1, v2, v3);
    }

    [Then("la última cotización de {string} tiene la versión {int}")]
    public async Task EntoncesLaVersion(string alias, int version)
    {
        var cotizacion = await contexto.ConsultarAsync(s =>
            s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdDelUsuarioAsync(_ultimaPorComercial[alias], contexto.Usuarios[alias]));
        cotizacion!.Version.Should().Be(version);
    }

    [Then("se crean {int} folios distintos del {string} al {string}")]
    public void EntoncesFoliosDistintos(int cantidad, string desde, string hasta)
    {
        _error.Should().BeNull(_error?.Mensaje);
        var prefijo = desde[..^3];
        var esperados = Enumerable.Range(int.Parse(desde[^3..], CultureInfo.InvariantCulture), cantidad)
            .Select(n => $"{prefijo}{n.ToString("000", CultureInfo.InvariantCulture)}")
            .ToList();
        esperados[^1].Should().Be(hasta);
        _creados.Select(c => c.Folio).Should().BeEquivalentTo(esperados);
    }

    [Then("la creación se rechaza con el código {string}")]
    public void EntoncesSeRechaza(string codigo)
    {
        _error.Should().NotBeNull("se esperaba el rechazo {0}", codigo);
        _error!.Codigo.Should().Be(codigo);
    }

    private static DateTime Leer(string fechaHora) =>
        DateTime.ParseExact(fechaHora, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private async Task CrearAsync(string alias, string? nit) => Registrar(alias, await GenerarAsync(contexto.Usuarios[alias], nit));

    private async Task CrearVersionAsync(string alias, Guid origen)
    {
        var usuarioId = contexto.Usuarios[alias];
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsGeneradorFolioService>().CrearNuevaVersionAsync<clsCotizacionSolicitud>(
            usuarioId, origen, (folio, ct) => GuardarAsync(s, new clsDatosCotizacion { Folio = folio.Valor, Version = folio.Version, UsuarioId = usuarioId }, ct)));
        Registrar(alias, resultado);
    }

    private Task<clsResultado<clsCotizacionSolicitud>> GenerarAsync(Guid usuarioId, string? nit) =>
        contexto.ConsultarAsync(s => s.GetRequiredService<IClsGeneradorFolioService>().CrearConFolioNuevoAsync<clsCotizacionSolicitud>(
            usuarioId, nit, (folio, ct) => GuardarAsync(s, new clsDatosCotizacion { Folio = folio.Valor, Version = folio.Version, UsuarioId = usuarioId, NitCliente = nit }, ct)));

    private static async Task<clsResultado<clsCotizacionSolicitud>> GuardarAsync(IServiceProvider servicios, clsDatosCotizacion datos, CancellationToken ct)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(datos);
        if (cotizacion.EsFallo)
        {
            return cotizacion.Error;
        }

        servicios.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion.Valor);
        await servicios.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(ct);
        return cotizacion.Valor;
    }

    private void Registrar(string alias, clsResultado<clsCotizacionSolicitud> resultado)
    {
        if (resultado.EsFallo)
        {
            _error = resultado.Error;
            return;
        }

        _creados.Add((alias, resultado.Valor.Folio));
        _ultimaPorComercial[alias] = resultado.Valor.Id;
    }

    private Task<clsCotizacionSolicitud> LeerAsync(string folio) =>
        contexto.ConsultarAsync(async s => (await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio))!);
}
