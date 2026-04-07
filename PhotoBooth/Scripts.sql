select * from configurationentries
select * from events
select * from photos



go 
alter proc uspCreatePhoto  
 @uri varchar(1024)
as  
 declare @eventID int = (select [Value] from ConfigurationEntries where [Key] = 'EventID')
 insert photos (Uri, EventID) values (@uri, @eventID)
go
alter proc uspIndexGet 
as  
 declare @eventID int = (select [Value] from ConfigurationEntries where [Key] = 'EventID')
 select p.PhotoID, Uri, [Name], [Description], p.Phone, count(*) Votes 
  from Photos p left join Votes v on v.PhotoID = p.PhotoID 
  where p.EventID = @eventID
  group by p.PhotoID, Uri, [Name], [Description], p.Phone 
  order by p.PhotoID desc  
go  
alter proc uspVotesGet 
as  
 declare @eventID int = (select [Value] from ConfigurationEntries where [Key] = 'EventID')
 select p.PhotoID, Uri, [Name], [Description], p.Phone, count(*) Votes   
  from Photos p left join Votes v on v.PhotoID = p.PhotoID   
  where p.EventID = @eventID
  group by p.PhotoID, Uri, [Name], [Description], p.Phone   
  order by p.PhotoID desc  
go 
alter proc uspSubmit  
 @uri varchar(1024),  
 @phone char(10),  
 @name varchar(500)  
as  
 begin try  
 begin transaction  
  --update Photos set phone = null where phone = @phone  
  update Photos set phone = @phone, [Name] = @name where Uri = @uri  
  commit  
 end try  
 begin catch  
  rollback  
 end catch
go
alter proc uspVote  
 @uri varchar(1024),  
 @phone char(10)  
as  
 if exists(select * from Photos where Phone = @phone)  
 begin   
  declare @count int  
  select @count = count(*) from Votes where Phone = @phone   
  if @count > 5   
  begin   
   set @count = @count - 5  
   delete Votes where VoteID in (select top(@count) voteid from Votes where Phone = @phone order by VoteID asc)  
  end   
  declare @photoID int  
  select @photoID = photoID from Photos where Uri = @uri  
  if (@photoID is not null)  
  begin   
   insert Votes (PhotoID, Phone) values (@photoID, @phone)  
  end  
 end  

