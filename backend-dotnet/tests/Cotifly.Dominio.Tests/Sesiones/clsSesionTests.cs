using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Sesiones;

namespace Cotifly.Dominio.Tests.Sesiones;

public sealed class clsSesionTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnaSesionNuevaQuedaActivaConSuActividadYDueno()
    {
        var usuario = Guid.NewGuid();

        var sesion = clsSesion.Crear("spaces/AAA", "spaces/AAA/threads/1", _ahora, usuario).Valor;

        sesion.Estado.Should().Be(EstadoSesion.Activa);
        sesion.UltimaActividad.Should().Be(_ahora);
        sesion.EsDe(usuario).Should().BeTrue();
        sesion.EstadoAgente.Should().BeNull();
    }

    [Fact]
    public void LaSesionWebUsaElMismoIdComoSpaceYThread()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        clsSesion.IdWeb(id).Should().Be("web/11111111-2222-3333-4444-555555555555");
    }

    [Theory]
    [InlineData("", "t")]
    [InlineData("s", " ")]
    public void SpaceYThreadSonObligatorios(string space, string hilo) =>
        clsSesion.Crear(space, hilo, _ahora).Error.Codigo.Should().Be(clsSesion.CodigoError);

    [Fact]
    public void SpaceYThreadNoSuperan128Caracteres() =>
        clsSesion.Crear(new string('s', 129), "t", _ahora).EsFallo.Should().BeTrue();

    [Fact]
    public void TocarActualizaLaActividadYReactivaUnaSesionArchivada()
    {
        var sesion = clsSesion.Crear("s", "t", _ahora).Valor;
        sesion.Archivar();
        sesion.Estado.Should().Be(EstadoSesion.Archivada);

        sesion.Tocar(_ahora.AddHours(1));

        sesion.UltimaActividad.Should().Be(_ahora.AddHours(1));
        sesion.Estado.Should().Be(EstadoSesion.Activa);
    }

    [Fact]
    public void UnaSesionConDuenoNoSePuedeAsignarAOtroUsuario()
    {
        var dueno = Guid.NewGuid();
        var sesion = clsSesion.Crear("s", "t", _ahora, dueno).Valor;

        sesion.AsignarUsuario(Guid.NewGuid()).Should().BeFalse();
        sesion.AsignarUsuario(dueno).Should().BeTrue();
        sesion.UsuarioId.Should().Be(dueno);
    }

    [Fact]
    public void UnaSesionSinDuenoSeLigaAlPrimerUsuario()
    {
        var sesion = clsSesion.Crear("s", "t", _ahora).Valor;
        var usuario = Guid.NewGuid();

        sesion.AsignarUsuario(usuario).Should().BeTrue();
        sesion.EsDe(usuario).Should().BeTrue();
    }

    [Fact]
    public void ElEstadoDelAgenteSeReemplazaCompleto()
    {
        var sesion = clsSesion.Crear("s", "t", _ahora).Valor;
        var estado = clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-NV-24092026-001", CotizacionId = Guid.NewGuid() };

        sesion.ActualizarEstadoAgente(estado);

        sesion.EstadoAgente.Should().BeSameAs(estado);
        FluentActions.Invoking(() => sesion.ActualizarEstadoAgente(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UnEventoExigeRolYFijaCreatedAtSiSeIndica()
    {
        clsEventoConversacion.Crear("s", "t", " ", "hola").EsFallo.Should().BeTrue();

        var evento = clsEventoConversacion.Crear("s", "t", clsEventoConversacion.RolUsuario, "hola", cuando: _ahora).Valor;

        evento.Rol.Should().Be("user");
        evento.CreatedAt.Should().Be(_ahora);
        clsEventoConversacion.Crear("s", "t", "assistant", "hola").Valor.CreatedAt.Should().Be(default, "sin 'cuando' lo asigna la persistencia");
        clsEventoConversacion.Crear("s", "t", clsEventoConversacion.RolHerramienta, null).Valor.Rol.Should().Be("herramienta");
    }

    // HU-013.9 (#105): el estado es solo el folio de la conversación (sin slot-filling) y compara por valor.
    [Fact]
    public void ElEstadoDeConversacionEsElFolioYComparaPorValor()
    {
        var id = Guid.NewGuid();
        var a = clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-NV-24092026-001", CotizacionId = id, Version = 1, Moneda = "COP" };
        var b = clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-NV-24092026-001", CotizacionId = id, Version = 1, Moneda = "COP" };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(a with { Version = 2 });
        typeof(clsEstadoConversacion).GetProperties().Where(p => p.CanWrite).Select(p => p.Name)
            .Should().BeEquivalentTo(["FolioEnProgreso", "CotizacionId", "Version", "Moneda"], "el slot-filling del agente legado se retiró");
    }
}
